using System.Net;
using System.Text.Json;
using BookWorm.Contracts;
using BookWorm.UI.Api;
using Microsoft.JSInterop;

namespace BookWorm.UI.Services;

/// <summary>A file chosen in the browser, not uploaded yet. <see cref="Key"/> refers to it in <c>files.js</c>.</summary>
public sealed record PickedFile(string Key, string Name, long Size, string Type);

/// <summary>What an ebook file says about itself.</summary>
/// <param name="Published">A full date (yyyy-MM-dd) when the file has one.</param>
/// <param name="PublishedText">The date as written in the file, e.g. just a year.</param>
/// <param name="CoverKey">The file's cover, scaled down, ready to upload.</param>
public sealed record FileMetadata(
    string Title,
    List<string> Authors,
    string Published,
    string PublishedText,
    string Language,
    string CoverKey);

/// <summary>
/// Picks, inspects and uploads files through <c>files.js</c>. Uploads go straight from the browser to
/// the server (with progress), so even very large books never pass through the app's memory.
/// </summary>
public sealed class BrowserFiles(IJSRuntime js) : IAsyncDisposable
{
    private Task<IJSObjectReference> _module;

    private Task<IJSObjectReference> Module =>
        _module ??= js.InvokeAsync<IJSObjectReference>("import", "./_content/BookWorm.UI/js/files.js").AsTask();

    public async Task<PickedFile> PickAsync(string accept) =>
        await (await Module).InvokeAsync<PickedFile>("pickFile", accept);

    public async Task<FileMetadata> ReadMetadataAsync(string key, BookFormat format) =>
        await (await Module).InvokeAsync<FileMetadata>("readMetadata", key, format.ToString());

    public async Task<string> ObjectUrlAsync(string key) =>
        await (await Module).InvokeAsync<string>("objectUrl", key);

    public async Task ReleaseAsync(string key)
    {
        if (key is not null)
        {
            await (await Module).InvokeVoidAsync("release", key);
        }
    }

    /// <summary>A scaled-down JPEG copy of a picked image, for use as a cover.</summary>
    public async Task<string> PrepareCoverAsync(string key) =>
        await (await Module).InvokeAsync<string>("prepareCover", key);

    /// <summary>The cover inside a book file that is already on the server, or null when it has none.</summary>
    public async Task<string> CoverFromBookFileAsync(Guid bookId, BookFileDetails file) =>
        await (await Module).InvokeAsync<string>("coverFromUrl", BookWormApiClient.FileUrl(bookId, file.Id), file.FileName, file.Format.ToString());

    /// <summary>Uploads a picked file as a request body. Throws <see cref="ApiException"/> when the server refuses it.</summary>
    public async Task<T> UploadAsync<T>(string key, string url, Action<long, long> onProgress = null)
    {
        using var progress = DotNetObjectReference.Create(new UploadProgress(onProgress));
        var result = await (await Module).InvokeAsync<UploadResult>("upload", key, url, progress);
        if (result.Status is >= 200 and < 300)
        {
            return JsonSerializer.Deserialize<T>(result.Body, BookWormJson.Options)
                ?? throw new ApiException((HttpStatusCode)result.Status, null, "The server returned an empty response.");
        }

        ApiProblem problem = null;
        try
        {
            problem = JsonSerializer.Deserialize<ApiProblem>(result.Body, BookWormJson.Options);
        }
        catch (JsonException)
        {
            // Not problem details (e.g. a proxy's error page).
        }

        throw result.Status == 0
            ? new HttpRequestException("The upload failed.")
            : new ApiException((HttpStatusCode)result.Status, problem, result.Status == 413 && problem?.Detail is null
                ? "The file is too large for the server."
                : null);
    }

    public async ValueTask DisposeAsync()
    {
        if (_module is { IsCompletedSuccessfully: true })
        {
            try
            {
                await _module.Result.DisposeAsync();
            }
            catch (JSDisconnectedException)
            {
                // The page is gone already.
            }
        }
    }

    private sealed record UploadResult(int Status, string Body);

    private sealed class UploadProgress(Action<long, long> onProgress)
    {
        [JSInvokable]
        public void OnUploadProgress(long loaded, long total) => onProgress?.Invoke(loaded, total);
    }
}
