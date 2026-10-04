using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using BookWorm.Contracts;

namespace BookWorm.UI.Api;

/// <summary>
/// Typed client for the BookWorm HTTP API, used by every screen. The host decides how requests are
/// authenticated (the web app relies on the browser's session cookie).
/// </summary>
public sealed class BookWormApiClient(HttpClient http)
{
    private static readonly JsonSerializerOptions Json = BookWormJson.Options;

    // Server and account

    // App sign-in (bearer tokens; the web app uses the server's login pages and a cookie instead)

    public Task<AppTokens> LoginAsync(AppLoginRequest request, CancellationToken cancellationToken = default) =>
        SendAsync<AppTokens>(HttpMethod.Post, "api/auth/login", request, cancellationToken);

    public Task<AppTokens> RefreshTokensAsync(string refreshToken, CancellationToken cancellationToken = default) =>
        SendAsync<AppTokens>(HttpMethod.Post, "api/auth/refresh", new RefreshTokenRequest { RefreshToken = refreshToken }, cancellationToken);

    public Task<AppTokens> RedeemMobileCodeAsync(MobileCodeRequest request, CancellationToken cancellationToken = default) =>
        SendAsync<AppTokens>(HttpMethod.Post, "api/auth/mobile-code", request, cancellationToken);

    public Task SignOutEverywhereAsync(CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Post, "api/account/sign-out-everywhere", null, cancellationToken);

    public Task<ServerInfo> GetServerInfoAsync(CancellationToken cancellationToken = default) =>
        GetAsync<ServerInfo>("api/server-info", cancellationToken);

    /// <summary>The signed-in user, or null when the session is missing or expired.</summary>
    public async Task<CurrentUser> GetCurrentUserAsync(CancellationToken cancellationToken = default)
    {
        using var response = await http.GetAsync("api/me", cancellationToken);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            return null;
        }

        return await ReadAsync<CurrentUser>(response, cancellationToken);
    }

    public Task LogoutAsync(CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Post, "api/account/logout", null, cancellationToken);

    // Books

    public Task<PagedResult<BookSummary>> GetBooksAsync(BookListQuery query = null, CancellationToken cancellationToken = default) =>
        GetAsync<PagedResult<BookSummary>>("api/books" + QueryStrings.For(query), cancellationToken);

    public Task<BookDetails> GetBookAsync(Guid id, CancellationToken cancellationToken = default) =>
        GetAsync<BookDetails>($"api/books/{id}", cancellationToken);

    public Task<BookDetails> CreateBookAsync(CreateBookRequest request, CancellationToken cancellationToken = default) =>
        SendAsync<BookDetails>(HttpMethod.Post, "api/books", request, cancellationToken);

    public Task<BookDetails> UpdateBookAsync(Guid id, UpdateBookRequest request, CancellationToken cancellationToken = default) =>
        SendAsync<BookDetails>(HttpMethod.Put, $"api/books/{id}", request, cancellationToken);

    public Task DeleteBookAsync(Guid id, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Delete, $"api/books/{id}", null, cancellationToken);

    // Files and covers (the web app uploads large files from JavaScript, with progress; see files.js)

    public async Task<BookFileDetails> UploadFileAsync(Guid bookId, BookFormat format, Stream content, string fileName, CancellationToken cancellationToken = default)
    {
        using var body = new StreamContent(content);
        body.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        using var response = await http.PutAsync(FileUploadUrl(bookId, format, fileName), body, cancellationToken);
        return await ReadAsync<BookFileDetails>(response, cancellationToken);
    }

    public Task DeleteFileAsync(Guid bookId, Guid fileId, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Delete, $"api/books/{bookId}/files/{fileId}", null, cancellationToken);

    public async Task<CoverDetails> UploadCoverAsync(Guid bookId, Stream image, string contentType, CancellationToken cancellationToken = default)
    {
        using var body = new StreamContent(image);
        body.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        using var response = await http.PutAsync($"api/books/{bookId}/cover", body, cancellationToken);
        return await ReadAsync<CoverDetails>(response, cancellationToken);
    }

    public Task DeleteCoverAsync(Guid bookId, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Delete, $"api/books/{bookId}/cover", null, cancellationToken);

    public static string FileUploadUrl(Guid bookId, BookFormat format, string fileName) =>
        $"api/books/{bookId}/files/{format.ToString().ToLowerInvariant()}?fileName={Uri.EscapeDataString(fileName)}";

    public static string FileUrl(Guid bookId, Guid fileId, bool download = false) =>
        $"api/books/{bookId}/files/{fileId}" + (download ? "?download=true" : "");

    public static string CoverUrl(Guid bookId, long? coverVersion) =>
        coverVersion is { } version ? $"api/books/{bookId}/cover?v={version}" : null;

    public static string CoverUploadUrl(Guid bookId) => $"api/books/{bookId}/cover";

    public static string NotesUrl(Guid bookId) => $"api/books/{bookId}/notes.md";

    public const string NotesExportUrl = "api/notes/export.zip";

    // Highlights

    public Task<List<HighlightDetails>> GetHighlightsAsync(Guid bookId, CancellationToken cancellationToken = default) =>
        GetAsync<List<HighlightDetails>>($"api/books/{bookId}/highlights", cancellationToken);

    public Task<HighlightDetails> CreateHighlightAsync(Guid bookId, CreateHighlightRequest request, CancellationToken cancellationToken = default) =>
        SendAsync<HighlightDetails>(HttpMethod.Post, $"api/books/{bookId}/highlights", request, cancellationToken);

    public Task<HighlightDetails> UpdateHighlightAsync(Guid bookId, Guid id, UpdateHighlightRequest request, CancellationToken cancellationToken = default) =>
        SendAsync<HighlightDetails>(HttpMethod.Put, $"api/books/{bookId}/highlights/{id}", request, cancellationToken);

    public Task<HighlightDetails> ReanchorHighlightAsync(Guid bookId, Guid id, ReanchorHighlightRequest request, CancellationToken cancellationToken = default) =>
        SendAsync<HighlightDetails>(HttpMethod.Put, $"api/books/{bookId}/highlights/{id}/anchor", request, cancellationToken);

    public Task DeleteHighlightAsync(Guid bookId, Guid id, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Delete, $"api/books/{bookId}/highlights/{id}", null, cancellationToken);

    // Reading

    /// <summary>The read in progress for a book, started now if there is none.</summary>
    /// <summary>The read in progress, or null when there is none.</summary>
    public async Task<ReadDetails> GetCurrentReadAsync(Guid bookId, CancellationToken cancellationToken = default)
    {
        using var response = await http.GetAsync($"api/books/{bookId}/reads/current", cancellationToken);
        return response.StatusCode == HttpStatusCode.NoContent ? null : await ReadAsync<ReadDetails>(response, cancellationToken);
    }

    /// <summary>Starts a read with an id and start time chosen by the app (for reads started offline).</summary>
    public Task<ReadDetails> StartOrResumeReadAsync(Guid bookId, StartReadRequest request, CancellationToken cancellationToken = default) =>
        SendAsync<ReadDetails>(HttpMethod.Post, $"api/books/{bookId}/reads/current", request, cancellationToken);

    public Task<ReadDetails> StartOrResumeReadAsync(Guid bookId, CancellationToken cancellationToken = default) =>
        SendAsync<ReadDetails>(HttpMethod.Post, $"api/books/{bookId}/reads/current", null, cancellationToken);

    public Task SaveProgressAsync(Guid bookId, Guid readId, ReadProgressRequest request, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Put, ProgressUrl(bookId, readId), request, cancellationToken);

    public static string ProgressUrl(Guid bookId, Guid readId) => $"api/books/{bookId}/reads/{readId}/progress";

    public Task SaveBrowsePositionAsync(Guid bookId, Guid fileId, BrowsePositionRequest request, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Put, BrowsePositionUrl(bookId, fileId), request, cancellationToken);

    public static string BrowsePositionUrl(Guid bookId, Guid fileId) => $"api/books/{bookId}/files/{fileId}/position";

    public Task<ReadDetails> FinishReadAsync(Guid bookId, Guid readId, FinishReadRequest request, CancellationToken cancellationToken = default) =>
        SendAsync<ReadDetails>(HttpMethod.Post, $"api/books/{bookId}/reads/{readId}/finish", request, cancellationToken);

    public Task<List<CurrentRead>> GetCurrentReadsAsync(CancellationToken cancellationToken = default) =>
        GetAsync<List<CurrentRead>>("api/reading", cancellationToken);

    public Task<ReadingStats> GetStatsAsync(int? year, string timeZone, CancellationToken cancellationToken = default) =>
        GetAsync<ReadingStats>(
            "api/stats" + QueryStrings.Build(("year", year?.ToString(CultureInfo.InvariantCulture)), ("timeZone", timeZone)),
            cancellationToken);

    // Reads

    public Task<List<ReadDetails>> GetReadsAsync(Guid bookId, CancellationToken cancellationToken = default) =>
        GetAsync<List<ReadDetails>>($"api/books/{bookId}/reads", cancellationToken);

    public Task<ReadDetails> CreateReadAsync(Guid bookId, CreateReadRequest request, CancellationToken cancellationToken = default) =>
        SendAsync<ReadDetails>(HttpMethod.Post, $"api/books/{bookId}/reads", request, cancellationToken);

    public Task<ReadDetails> UpdateReadAsync(Guid bookId, Guid readId, UpdateReadRequest request, CancellationToken cancellationToken = default) =>
        SendAsync<ReadDetails>(HttpMethod.Put, $"api/books/{bookId}/reads/{readId}", request, cancellationToken);

    public Task DeleteReadAsync(Guid bookId, Guid readId, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Delete, $"api/books/{bookId}/reads/{readId}", null, cancellationToken);

    // Authors

    public Task<PagedResult<AuthorSummary>> GetAuthorsAsync(AuthorListQuery query = null, CancellationToken cancellationToken = default) =>
        GetAsync<PagedResult<AuthorSummary>>("api/authors" + QueryStrings.For(query), cancellationToken);

    public Task<AuthorDetails> GetAuthorAsync(Guid id, CancellationToken cancellationToken = default) =>
        GetAsync<AuthorDetails>($"api/authors/{id}", cancellationToken);

    public Task<AuthorDetails> CreateAuthorAsync(CreateAuthorRequest request, CancellationToken cancellationToken = default) =>
        SendAsync<AuthorDetails>(HttpMethod.Post, "api/authors", request, cancellationToken);

    public Task<AuthorDetails> UpdateAuthorAsync(Guid id, UpdateAuthorRequest request, CancellationToken cancellationToken = default) =>
        SendAsync<AuthorDetails>(HttpMethod.Put, $"api/authors/{id}", request, cancellationToken);

    public Task DeleteAuthorAsync(Guid id, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Delete, $"api/authors/{id}", null, cancellationToken);

    // Tags

    public Task<List<TagSummary>> GetTagsAsync(string search = null, CancellationToken cancellationToken = default) =>
        GetAsync<List<TagSummary>>("api/tags" + QueryStrings.ForSearch(search), cancellationToken);

    public Task<TagSummary> CreateTagAsync(CreateTagRequest request, CancellationToken cancellationToken = default) =>
        SendAsync<TagSummary>(HttpMethod.Post, "api/tags", request, cancellationToken);

    public Task<TagSummary> UpdateTagAsync(Guid id, UpdateTagRequest request, CancellationToken cancellationToken = default) =>
        SendAsync<TagSummary>(HttpMethod.Put, $"api/tags/{id}", request, cancellationToken);

    public Task DeleteTagAsync(Guid id, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Delete, $"api/tags/{id}", null, cancellationToken);

    // Collections

    public Task<List<CollectionSummary>> GetCollectionsAsync(string search = null, CancellationToken cancellationToken = default) =>
        GetAsync<List<CollectionSummary>>("api/collections" + QueryStrings.ForSearch(search), cancellationToken);

    public Task<CollectionDetails> GetCollectionAsync(Guid id, CancellationToken cancellationToken = default) =>
        GetAsync<CollectionDetails>($"api/collections/{id}", cancellationToken);

    public Task<CollectionDetails> CreateCollectionAsync(CreateCollectionRequest request, CancellationToken cancellationToken = default) =>
        SendAsync<CollectionDetails>(HttpMethod.Post, "api/collections", request, cancellationToken);

    public Task<CollectionDetails> UpdateCollectionAsync(Guid id, UpdateCollectionRequest request, CancellationToken cancellationToken = default) =>
        SendAsync<CollectionDetails>(HttpMethod.Put, $"api/collections/{id}", request, cancellationToken);

    /// <summary>Replaces the collection's books with these, in this order.</summary>
    public Task<CollectionDetails> SetCollectionBooksAsync(Guid id, IEnumerable<Guid> bookIds, CancellationToken cancellationToken = default) =>
        SendAsync<CollectionDetails>(HttpMethod.Put, $"api/collections/{id}/books", new SetCollectionBooksRequest { BookIds = bookIds.ToList() }, cancellationToken);

    public Task DeleteCollectionAsync(Guid id, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Delete, $"api/collections/{id}", null, cancellationToken);

    // Administration

    public Task<List<AdminUser>> GetUsersAsync(CancellationToken cancellationToken = default) =>
        GetAsync<List<AdminUser>>("api/admin/users", cancellationToken);

    public Task<AdminUser> CreateUserAsync(CreateUserRequest request, CancellationToken cancellationToken = default) =>
        SendAsync<AdminUser>(HttpMethod.Post, "api/admin/users", request, cancellationToken);

    public Task<AdminUser> UpdateUserAsync(Guid id, UpdateUserRequest request, CancellationToken cancellationToken = default) =>
        SendAsync<AdminUser>(HttpMethod.Put, $"api/admin/users/{id}", request, cancellationToken);

    public Task ResetPasswordAsync(Guid id, ResetPasswordRequest request, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Put, $"api/admin/users/{id}/password", request, cancellationToken);

    /// <summary>Turns off a user's two-factor authentication, e.g. after they lost their authenticator app.</summary>
    public Task ResetTwoFactorAsync(Guid id, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Delete, $"api/admin/users/{id}/two-factor", null, cancellationToken);

    public Task DeleteUserAsync(Guid id, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Delete, $"api/admin/users/{id}", null, cancellationToken);

    public Task<BackupOverview> GetBackupsAsync(CancellationToken cancellationToken = default) =>
        GetAsync<BackupOverview>("api/admin/backups", cancellationToken);

    public Task<BackupOverview> UpdateBackupSettingsAsync(UpdateBackupSettingsRequest request, CancellationToken cancellationToken = default) =>
        SendAsync<BackupOverview>(HttpMethod.Put, "api/admin/backups/settings", request, cancellationToken);

    public Task<BackupOverview> StartBackupAsync(CancellationToken cancellationToken = default) =>
        SendAsync<BackupOverview>(HttpMethod.Post, "api/admin/backups", null, cancellationToken);

    public Task DeleteBackupAsync(string name, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Delete, $"api/admin/backups/{Uri.EscapeDataString(name)}", null, cancellationToken);

    /// <summary>Asks the server to restore a backup. It restarts to do so; expect it to be unreachable for a while.</summary>
    public Task RestoreBackupAsync(string name, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Post, $"api/admin/backups/{Uri.EscapeDataString(name)}/restore", new RestoreBackupRequest { Confirm = true }, cancellationToken);

    public static string BackupUrl(string name) => $"api/admin/backups/{Uri.EscapeDataString(name)}";

    public const string BackupUploadUrl = "api/admin/backups/upload";

    // Plumbing

    private async Task<T> GetAsync<T>(string uri, CancellationToken cancellationToken)
    {
        using var response = await http.GetAsync(uri, cancellationToken);
        return await ReadAsync<T>(response, cancellationToken);
    }

    private async Task<T> SendAsync<T>(HttpMethod method, string uri, object body, CancellationToken cancellationToken)
    {
        using var response = await SendCoreAsync(method, uri, body, cancellationToken);
        return await ReadAsync<T>(response, cancellationToken);
    }

    private async Task SendAsync(HttpMethod method, string uri, object body, CancellationToken cancellationToken)
    {
        using var response = await SendCoreAsync(method, uri, body, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    private async Task<HttpResponseMessage> SendCoreAsync(HttpMethod method, string uri, object body, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, uri);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, body.GetType(), options: Json);
        }

        return await http.SendAsync(request, cancellationToken);
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<T>(Json, cancellationToken)
            ?? throw new ApiException(response.StatusCode, null, "The server returned an empty response.");
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        ApiProblem problem = null;
        if (response.Content.Headers.ContentType?.MediaType is "application/problem+json" or "application/json")
        {
            try
            {
                problem = await response.Content.ReadFromJsonAsync<ApiProblem>(Json, cancellationToken);
            }
            catch (JsonException)
            {
                // Not a problem-details body; report the status code alone.
            }
        }

        throw new ApiException(response.StatusCode, problem);
    }
}

/// <summary>An error response from the API, with the server's problem details when it sent them.</summary>
public sealed class ApiException(HttpStatusCode statusCode, ApiProblem problem, string message = null)
    : Exception(message ?? problem?.Detail ?? problem?.Title ?? $"The server responded with {(int)statusCode} ({statusCode}).")
{
    public HttpStatusCode StatusCode { get; } = statusCode;

    public ApiProblem Problem { get; } = problem;

    /// <summary>Validation errors keyed by camelCase field name; empty for other errors.</summary>
    public IReadOnlyDictionary<string, string[]> Errors => Problem?.Errors ?? [];
}
