using System.Net;
using System.Text.Json;
using BookWorm.Contracts;
using BookWorm.Mobile.Core.Offline;
using BookWorm.UI.Api;

namespace BookWorm.Mobile.Core.Tests;

public sealed class SyncServiceTests : IDisposable
{
    private readonly OfflineFixture _app = new();

    public void Dispose() => _app.Dispose();

    private SyncService Sync() => new(_app.Store, _app.Queue, _app.Session, _app.Connectivity, () => _app.Server.Client());

    [Fact]
    public async Task QueuedChanges_AreSentInOrder_AndRemoved()
    {
        var (bookId, file) = _app.AddDownloadedBook();
        _app.Connectivity.IsOnline = false;
        var api = new BookWormApiClient(_app.AppClient());
        var read = await api.StartOrResumeReadAsync(bookId);
        await api.SaveProgressAsync(bookId, read.Id, new ReadProgressRequest { FileId = file.Id, Location = "x", Progress = 0.3 });
        _app.Connectivity.IsOnline = true;
        _app.Server.Respond = (request, _) => request.Method == HttpMethod.Get
            ? RefreshAnswer(request, bookId)
            : new HttpResponseMessage(HttpStatusCode.NoContent);

        await Sync().SyncAsync();

        var sent = _app.Server.Requests.Where(r => r.Method != HttpMethod.Get).Select(r => r.Path).ToList();
        Assert.Equal([$"api/books/{bookId}/reads/current", $"api/books/{bookId}/reads/{read.Id}/progress"], sent);
        Assert.Empty(_app.Queue.ReadAll(OfflineFixture.UserId));
    }

    [Fact]
    public async Task ChangesToAHighlightCreatedOffline_UseTheVersionTheServerGaveIt()
    {
        var (bookId, file) = _app.AddDownloadedBook();
        _app.Connectivity.IsOnline = false;
        var api = new BookWormApiClient(_app.AppClient());
        var created = await api.CreateHighlightAsync(bookId, new CreateHighlightRequest { FileId = file.Id, Location = "cfi", Text = "whale" });
        await api.UpdateHighlightAsync(bookId, created.Id, new UpdateHighlightRequest { Color = HighlightColor.Blue, Version = created.Version });
        _app.Connectivity.IsOnline = true;
        _app.Server.Respond = (request, _) => request.Method == HttpMethod.Post
            ? FakeServer.Json(created with { Version = 77 }, HttpStatusCode.Created)
            : request.Method == HttpMethod.Put ? FakeServer.Json(created with { Version = 78 }) : RefreshAnswer(request, bookId);

        await Sync().SyncAsync();

        var update = _app.Server.Requests.Single(r => r.Method == HttpMethod.Put);
        Assert.Equal(77u, JsonSerializer.Deserialize<UpdateHighlightRequest>(update.Body, BookWormJson.Options).Version);
    }

    [Fact]
    public async Task ChangesTheServerRefuses_AreDropped_AndReported()
    {
        var (bookId, _) = _app.AddDownloadedBook();
        _app.Queue.Enqueue(OfflineFixture.UserId, bookId, HttpMethod.Delete, $"api/books/{bookId}/highlights/{Guid.NewGuid()}", null);
        _app.Server.Respond = (request, _) => request.Method == HttpMethod.Delete ? new HttpResponseMessage(HttpStatusCode.NotFound) : RefreshAnswer(request, bookId);
        var sync = Sync();
        var dropped = 0;
        sync.ChangesDropped += count => dropped = count;

        await sync.SyncAsync();

        Assert.Equal(1, dropped);
        Assert.Empty(_app.Queue.ReadAll(OfflineFixture.UserId));
    }

    [Fact]
    public async Task WhenTheServerStruggles_ChangesStayQueued()
    {
        var (bookId, _) = _app.AddDownloadedBook();
        _app.Queue.Enqueue(OfflineFixture.UserId, bookId, HttpMethod.Delete, $"api/books/{bookId}/highlights/{Guid.NewGuid()}", null);
        _app.Server.Respond = (_, _) => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);

        await Sync().SyncAsync();

        Assert.Single(_app.Queue.ReadAll(OfflineFixture.UserId));
    }

    [Fact]
    public async Task Offline_NothingIsSent()
    {
        var (bookId, _) = _app.AddDownloadedBook();
        _app.Queue.Enqueue(OfflineFixture.UserId, bookId, HttpMethod.Delete, $"api/books/{bookId}/highlights/{Guid.NewGuid()}", null);
        _app.Connectivity.IsOnline = false;

        await Sync().SyncAsync();

        Assert.Empty(_app.Server.Requests);
    }

    [Fact]
    public async Task AfterSending_DownloadsAreRefreshedFromTheServer()
    {
        var (bookId, _) = _app.AddDownloadedBook();
        _app.Server.Respond = (request, _) => RefreshAnswer(request, bookId, title: "Renamed");

        await Sync().SyncAsync();

        Assert.Equal("Renamed", _app.Store.Read<BookDetails>(OfflineFixture.UserId, bookId, OfflineStore.BookJson).Title);
    }

    private HttpResponseMessage RefreshAnswer(HttpRequestMessage request, Guid bookId, string title = null)
    {
        var path = request.RequestUri.AbsolutePath;
        if (path.EndsWith("/highlights"))
        {
            return FakeServer.Json(new List<HighlightDetails>());
        }

        if (path.EndsWith("/reads/current"))
        {
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        }

        var book = _app.Store.Read<BookDetails>(OfflineFixture.UserId, bookId, OfflineStore.BookJson);
        return FakeServer.Json(title is null ? book : book with { Title = title });
    }
}
