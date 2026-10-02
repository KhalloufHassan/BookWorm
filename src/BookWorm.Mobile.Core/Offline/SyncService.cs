using System.Net;
using System.Text;
using System.Text.Json;
using BookWorm.Contracts;
using BookWorm.Mobile.Core.Auth;
using BookWorm.UI.Api;

namespace BookWorm.Mobile.Core.Offline;

/// <summary>
/// Sends changes made offline to the server, in the order they were made, then refreshes the local
/// copies of downloaded books. Runs when the app starts or resumes and when the connection returns.
/// </summary>
/// <param name="server">
/// An HttpClient straight to the server (with the token, but without <see cref="OfflineHttpHandler"/>),
/// whose base address is the server address.
/// </param>
public sealed class SyncService(OfflineStore store, OfflineQueue queue, SessionManager session, IConnectivity connectivity, Func<HttpClient> server)
{
    private static readonly JsonSerializerOptions Json = BookWormJson.Options;
    private readonly SemaphoreSlim _running = new(1, 1);

    /// <summary>Some changes were refused by the server (e.g. the highlight was deleted elsewhere) and dropped.</summary>
    public event Action<int> ChangesDropped;

    /// <summary>Local copies were refreshed.</summary>
    public event Action Synced;

    public async Task SyncAsync(CancellationToken cancellationToken = default)
    {
        if (!connectivity.IsOnline || session.User?.Id is not { } userId || !await _running.WaitAsync(0, cancellationToken))
        {
            return;
        }

        try
        {
            using var http = server();
            if (await SendQueueAsync(http, userId, cancellationToken))
            {
                await RefreshDownloadsAsync(http, userId, cancellationToken);
                Synced?.Invoke();
            }
        }
        catch (HttpRequestException)
        {
            // Lost the connection midway; the rest goes next time.
        }
        finally
        {
            _running.Release();
        }
    }

    /// <summary>Sends every queued change. False when it had to stop (no connection, or signed out).</summary>
    internal async Task<bool> SendQueueAsync(HttpClient http, Guid userId, CancellationToken cancellationToken)
    {
        // Highlights created or changed during this run: later changes to them need their new version.
        var versions = new Dictionary<Guid, uint>();
        var dropped = 0;
        try
        {
            foreach (var (file, change) in queue.ReadAll(userId))
            {
                var route = ReaderRoute.Parse(new HttpMethod(change.Method), new Uri(http.BaseAddress, change.Path));
                using var request = new HttpRequestMessage(new HttpMethod(change.Method), change.Path);
                if (change.Body is not null)
                {
                    request.Content = new StringContent(WithKnownVersion(route, change.Body, versions), Encoding.UTF8, "application/json");
                }

                using var response = await http.SendAsync(request, cancellationToken);
                if (response.IsSuccessStatusCode)
                {
                    if (route?.Kind is ReaderRouteKind.CreateHighlight or ReaderRouteKind.UpdateHighlight
                        && await response.Content.ReadFromJsonOrDefaultAsync<HighlightDetails>(cancellationToken) is { } saved)
                    {
                        versions[saved.Id] = saved.Version;
                    }
                }
                else if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests
                         || (int)response.StatusCode >= 500)
                {
                    // Not signed in anymore, or the server is struggling: try again later.
                    return false;
                }
                else
                {
                    // Refused for good (deleted elsewhere, changed elsewhere…): the server's version wins.
                    dropped++;
                }

                queue.Remove(file);
            }

            return true;
        }
        finally
        {
            if (dropped > 0)
            {
                ChangesDropped?.Invoke(dropped);
            }
        }
    }

    /// <summary>Refreshes each downloaded book's details, highlights and current read from the server.</summary>
    internal async Task RefreshDownloadsAsync(HttpClient http, Guid userId, CancellationToken cancellationToken)
    {
        var api = new BookWormApiClient(http);
        foreach (var bookId in store.ListBooks(userId))
        {
            if (queue.Count(userId, bookId) > 0)
            {
                // Changes made while this ran; the next sync sends them first.
                continue;
            }

            try
            {
                store.Write(userId, bookId, OfflineStore.BookJson, await api.GetBookAsync(bookId, cancellationToken));
                store.Write(userId, bookId, OfflineStore.HighlightsJson, await api.GetHighlightsAsync(bookId, cancellationToken));
                store.Write(userId, bookId, OfflineStore.ReadJson, await api.GetCurrentReadAsync(bookId, cancellationToken));
            }
            catch (ApiException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
                // Deleted from the library: the download is kept until removed here.
            }
        }
    }

    private static string WithKnownVersion(ReaderRoute route, string body, Dictionary<Guid, uint> versions)
    {
        if (route?.Kind != ReaderRouteKind.UpdateHighlight || !versions.TryGetValue(route.ItemId, out var version))
        {
            return body;
        }

        var update = JsonSerializer.Deserialize<UpdateHighlightRequest>(body, Json);
        update.Version = version;
        return JsonSerializer.Serialize(update, Json);
    }
}

internal static class HttpContentExtensions
{
    public static async Task<T> ReadFromJsonOrDefaultAsync<T>(this HttpContent content, CancellationToken cancellationToken) where T : class
    {
        try
        {
            return await System.Net.Http.Json.HttpContentJsonExtensions.ReadFromJsonAsync<T>(content, BookWormJson.Options, cancellationToken);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
