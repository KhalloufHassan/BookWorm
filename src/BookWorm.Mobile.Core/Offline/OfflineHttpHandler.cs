using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BookWorm.Contracts;
using BookWorm.Mobile.Core.Auth;
using BookWorm.UI.Api;

namespace BookWorm.Mobile.Core.Offline;

/// <summary>
/// Lets the shared pages read downloaded books offline without knowing about it. It sits in front of
/// the app's HttpClient and, for the reader's API calls on a downloaded book (see <see cref="ReaderRoute"/>):
/// <list type="bullet">
/// <item>online, passes them on and keeps the book's local copy up to date with the answers;</item>
/// <item>offline (or when the server can't be reached), answers from the local copy, and queues
/// changes (reading position, reads, highlights and notes) for <see cref="SyncService"/>.</item>
/// </list>
/// Anything else fails with <see cref="OfflineException"/> while offline.
/// </summary>
public sealed class OfflineHttpHandler(
    OfflineStore store, OfflineQueue queue, SessionManager session, IConnectivity connectivity, TimeProvider timeProvider) : DelegatingHandler
{
    private static readonly JsonSerializerOptions Json = BookWormJson.Options;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var route = ReaderRoute.Parse(request.Method, request.RequestUri);
        if (route is null || session.User?.Id is not { } userId || !store.IsDownloaded(userId, route.BookId))
        {
            if (!connectivity.IsOnline)
            {
                throw new OfflineException();
            }

            return await base.SendAsync(request, cancellationToken);
        }

        // Buffered, so the body can be both sent and applied to the local copy.
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        if (connectivity.IsOnline)
        {
            try
            {
                var response = await base.SendAsync(request, cancellationToken);
                return response.IsSuccessStatusCode ? await KeepLocalCopyAsync(userId, route, body, response, cancellationToken) : response;
            }
            catch (HttpRequestException) when (!cancellationToken.IsCancellationRequested)
            {
                // Connected, but the server can't be reached: carry on offline.
            }
        }

        return AnswerOffline(userId, route, body, request);
    }

    // Online: keep the local copy in step with the server

    private async Task<HttpResponseMessage> KeepLocalCopyAsync(
        Guid userId, ReaderRoute route, string body, HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var content = response.StatusCode == HttpStatusCode.NoContent ? null : await response.Content.ReadAsStringAsync(cancellationToken);
        var bookId = route.BookId;
        switch (route.Kind)
        {
            case ReaderRouteKind.GetBook:
                store.Write(userId, bookId, OfflineStore.BookJson, Deserialize<BookDetails>(content));
                break;
            case ReaderRouteKind.GetHighlights:
                store.Write(userId, bookId, OfflineStore.HighlightsJson, Deserialize<List<HighlightDetails>>(content));
                break;
            case ReaderRouteKind.GetCurrentRead:
            case ReaderRouteKind.StartRead:
            case ReaderRouteKind.FinishRead:
                store.Write(userId, bookId, OfflineStore.ReadJson, content is null ? null : Deserialize<ReadDetails>(content));
                break;
            case ReaderRouteKind.SaveProgress:
                ApplyProgress(userId, route, Deserialize<ReadProgressRequest>(body));
                break;
            case ReaderRouteKind.SaveBrowsePosition:
                ApplyBrowsePosition(userId, route, Deserialize<BrowsePositionRequest>(body));
                break;
            case ReaderRouteKind.CreateHighlight:
            case ReaderRouteKind.UpdateHighlight:
                UpsertHighlight(userId, bookId, Deserialize<HighlightDetails>(content));
                break;
            case ReaderRouteKind.DeleteHighlight:
                RemoveHighlight(userId, bookId, route.ItemId);
                break;
        }

        if (content is null)
        {
            return response;
        }

        // The body was read above; hand the page an identical one.
        var copy = new HttpResponseMessage(response.StatusCode) { RequestMessage = response.RequestMessage, Version = response.Version };
        copy.Content = new StringContent(content, System.Text.Encoding.UTF8, response.Content.Headers.ContentType?.MediaType ?? "application/json");
        foreach (var (name, values) in response.Headers)
        {
            copy.Headers.TryAddWithoutValidation(name, values);
        }

        response.Dispose();
        return copy;
    }

    // Offline: answer from the local copy, and queue the changes

    private HttpResponseMessage AnswerOffline(Guid userId, ReaderRoute route, string body, HttpRequestMessage request)
    {
        var bookId = route.BookId;
        var now = timeProvider.GetUtcNow();
        switch (route.Kind)
        {
            case ReaderRouteKind.GetBook:
            {
                var book = store.Read<BookDetails>(userId, bookId, OfflineStore.BookJson) ?? throw new OfflineException();
                var download = store.Read<DownloadRecord>(userId, bookId, OfflineStore.FileJson);

                // Only the downloaded file can be opened offline, so it's the only one the reader sees.
                return Json200(request, book with { Files = download is null ? [] : [download.File] });
            }

            case ReaderRouteKind.GetHighlights:
                return Json200(request, store.Read<List<HighlightDetails>>(userId, bookId, OfflineStore.HighlightsJson) ?? []);

            case ReaderRouteKind.GetCurrentRead:
                return store.Read<ReadDetails>(userId, bookId, OfflineStore.ReadJson) is { Status: ReadStatus.CurrentlyReading } current
                    ? Json200(request, current)
                    : Empty(request, HttpStatusCode.NoContent);

            case ReaderRouteKind.StartRead:
            {
                if (store.Read<ReadDetails>(userId, bookId, OfflineStore.ReadJson) is { Status: ReadStatus.CurrentlyReading } inProgress)
                {
                    return Json200(request, inProgress);
                }

                var start = Deserialize<StartReadRequest>(body) ?? new StartReadRequest();
                start.Id = start.Id is { } id && id != Guid.Empty ? id : Guid.CreateVersion7();
                start.StartedAt ??= now;
                var read = new ReadDetails(start.Id.Value, bookId, ReadStatus.CurrentlyReading, start.StartedAt, null, null, null, null, start.StartedAt, now, now, 0);
                store.Write(userId, bookId, OfflineStore.ReadJson, read);
                queue.Enqueue(userId, bookId, HttpMethod.Post, route.Path, start);
                return JsonResponse(request, HttpStatusCode.Created, read);
            }

            case ReaderRouteKind.SaveProgress:
            {
                var progress = Deserialize<ReadProgressRequest>(body);
                progress.SavedAt ??= now;
                ApplyProgress(userId, route, progress);
                queue.Enqueue(userId, bookId, HttpMethod.Put, route.Path, progress, mergeKey: $"progress:{route.ItemId}:{progress.SessionId}");
                return Empty(request, HttpStatusCode.NoContent);
            }

            case ReaderRouteKind.FinishRead:
            {
                var finish = Deserialize<FinishReadRequest>(body) ?? new FinishReadRequest();
                finish.FinishedAt ??= now;
                ReadDetails finished = null;
                store.Update<ReadDetails>(userId, bookId, OfflineStore.ReadJson, read =>
                {
                    finished = read?.Id == route.ItemId ? read with { Status = finish.Status, FinishedAt = finish.FinishedAt, UpdatedAt = now } : read;
                    return finished;
                });
                if (finished?.Id != route.ItemId)
                {
                    return Empty(request, HttpStatusCode.NotFound);
                }

                if (finish.UpdateBookStatus)
                {
                    store.Update<BookDetails>(userId, bookId, OfflineStore.BookJson, book => book is null ? null : book with
                    {
                        Status = finish.Status == ReadStatus.Skipped ? BookStatus.Skipped : BookStatus.Finished,
                    });
                }

                queue.Enqueue(userId, bookId, HttpMethod.Post, route.Path, finish);
                return Json200(request, finished);
            }

            case ReaderRouteKind.SaveBrowsePosition:
            {
                var position = Deserialize<BrowsePositionRequest>(body);
                ApplyBrowsePosition(userId, route, position);
                queue.Enqueue(userId, bookId, HttpMethod.Put, route.Path, position, mergeKey: $"position:{route.ItemId}");
                return Empty(request, HttpStatusCode.NoContent);
            }

            case ReaderRouteKind.CreateHighlight:
            {
                var create = Deserialize<CreateHighlightRequest>(body);
                create.Id = create.Id is { } id && id != Guid.Empty ? id : Guid.CreateVersion7();
                var format = store.Read<DownloadRecord>(userId, bookId, OfflineStore.FileJson)?.File.Format ?? BookFormat.Epub;
                var highlight = new HighlightDetails(
                    create.Id.Value, bookId, create.FileId, format, create.Location, create.Text, create.Prefix, create.Suffix,
                    Blank(create.Chapter), Blank(create.PageLabel), create.Position, create.Color, Blank(create.Note),
                    HighlightState.Anchored, now, now, 0);
                UpsertHighlight(userId, bookId, highlight);
                queue.Enqueue(userId, bookId, HttpMethod.Post, route.Path, create);
                return JsonResponse(request, HttpStatusCode.Created, highlight);
            }

            case ReaderRouteKind.UpdateHighlight:
            {
                var update = Deserialize<UpdateHighlightRequest>(body);
                var existing = store.Read<List<HighlightDetails>>(userId, bookId, OfflineStore.HighlightsJson)?.FirstOrDefault(h => h.Id == route.ItemId);
                if (existing is null)
                {
                    return Empty(request, HttpStatusCode.NotFound);
                }

                var updated = existing with { Color = update.Color, Note = Blank(update.Note), UpdatedAt = now };
                UpsertHighlight(userId, bookId, updated);
                queue.Enqueue(userId, bookId, HttpMethod.Put, route.Path, update);
                return Json200(request, updated);
            }

            case ReaderRouteKind.DeleteHighlight:
                RemoveHighlight(userId, bookId, route.ItemId);
                queue.Enqueue(userId, bookId, HttpMethod.Delete, route.Path, null);
                return Empty(request, HttpStatusCode.NoContent);

            default:
                throw new OfflineException();
        }
    }

    // Changes to the local copy, the same online and offline

    private void ApplyProgress(Guid userId, ReaderRoute route, ReadProgressRequest progress) =>
        store.Update<ReadDetails>(userId, route.BookId, OfflineStore.ReadJson, read => read?.Id == route.ItemId
            ? read with
            {
                FileId = progress.FileId,
                Location = progress.Location,
                Progress = progress.Progress,
                LastOpenedAt = progress.SavedAt ?? timeProvider.GetUtcNow(),
            }
            : read);

    private void ApplyBrowsePosition(Guid userId, ReaderRoute route, BrowsePositionRequest position) =>
        store.Update<DownloadRecord>(userId, route.BookId, OfflineStore.FileJson, record => record?.File.Id == route.ItemId
            ? record with { File = record.File with { BrowseLocation = position.Location, BrowseProgress = position.Progress } }
            : record);

    private void UpsertHighlight(Guid userId, Guid bookId, HighlightDetails highlight)
    {
        if (highlight is null)
        {
            return;
        }

        store.Update<List<HighlightDetails>>(userId, bookId, OfflineStore.HighlightsJson, list =>
            (list ?? []).Where(h => h.Id != highlight.Id).Append(highlight).OrderBy(h => h.Position).ThenBy(h => h.CreatedAt).ToList());
    }

    private void RemoveHighlight(Guid userId, Guid bookId, Guid highlightId) =>
        store.Update<List<HighlightDetails>>(userId, bookId, OfflineStore.HighlightsJson, list => list?.Where(h => h.Id != highlightId).ToList());

    // Helpers

    private static T Deserialize<T>(string json) where T : class =>
        string.IsNullOrWhiteSpace(json) ? null : JsonSerializer.Deserialize<T>(json, Json);

    private static string Blank(string value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static HttpResponseMessage Json200<T>(HttpRequestMessage request, T value) => JsonResponse(request, HttpStatusCode.OK, value);

    private static HttpResponseMessage JsonResponse<T>(HttpRequestMessage request, HttpStatusCode status, T value) =>
        new(status) { RequestMessage = request, Content = JsonContent.Create(value, options: Json) };

    private static HttpResponseMessage Empty(HttpRequestMessage request, HttpStatusCode status) =>
        new(status) { RequestMessage = request };
}
