using System.Net;
using System.Text.Json;
using BookWorm.Contracts;
using BookWorm.Mobile.Core.Offline;
using BookWorm.UI.Api;

namespace BookWorm.Mobile.Core.Tests;

public sealed class OfflineHttpHandlerTests : IDisposable
{
    private readonly OfflineFixture _app = new();

    public void Dispose() => _app.Dispose();

    [Fact]
    public async Task Offline_TheBookComesFromTheDownload_WithOnlyTheDownloadedFile()
    {
        var (bookId, file) = _app.AddDownloadedBook();
        _app.Connectivity.IsOnline = false;

        var book = await new BookWormApiClient(_app.AppClient()).GetBookAsync(bookId);

        Assert.Equal("Moby-Dick", book.Title);
        Assert.Equal(file.Id, Assert.Single(book.Files).Id);
        Assert.Empty(_app.Server.Requests);
    }

    [Fact]
    public async Task Offline_OtherRequests_FailWithOfflineException()
    {
        _app.Connectivity.IsOnline = false;
        var api = new BookWormApiClient(_app.AppClient());

        await Assert.ThrowsAsync<OfflineException>(() => api.GetBookAsync(Guid.NewGuid()));
        await Assert.ThrowsAsync<OfflineException>(() => api.GetCurrentUserAsync());
    }

    [Fact]
    public async Task Offline_StartingAndReading_IsQueued_AndTheReaderSeesIt()
    {
        var (bookId, file) = _app.AddDownloadedBook();
        _app.Connectivity.IsOnline = false;
        var api = new BookWormApiClient(_app.AppClient());

        Assert.Null(await api.GetCurrentReadAsync(bookId));
        var read = await api.StartOrResumeReadAsync(bookId);
        var session = Guid.NewGuid();
        await api.SaveProgressAsync(bookId, read.Id, new ReadProgressRequest { FileId = file.Id, Location = "one", Progress = 0.1, SessionId = session });
        _app.Time.Now += TimeSpan.FromMinutes(5);
        await api.SaveProgressAsync(bookId, read.Id, new ReadProgressRequest { FileId = file.Id, Location = "two", Progress = 0.2, SessionId = session });

        var current = await api.GetCurrentReadAsync(bookId);
        Assert.Equal(read.Id, current.Id);
        Assert.Equal("two", current.Location);
        Assert.Equal(0.2, current.Progress);

        // The start, then only the latest position of that sitting.
        var queued = _app.Queue.ReadAll(OfflineFixture.UserId).Select(entry => entry.Change).ToList();
        Assert.Equal(2, queued.Count);
        Assert.Equal($"api/books/{bookId}/reads/current", queued[0].Path);
        Assert.Equal(read.Id, JsonSerializer.Deserialize<StartReadRequest>(queued[0].Body, BookWormJson.Options).Id);
        var progress = JsonSerializer.Deserialize<ReadProgressRequest>(queued[1].Body, BookWormJson.Options);
        Assert.Equal("two", progress.Location);
        Assert.Equal(_app.Time.Now, progress.SavedAt);
    }

    [Fact]
    public async Task Offline_Highlights_CanBeAddedChangedAndDeleted()
    {
        var (bookId, file) = _app.AddDownloadedBook();
        _app.Connectivity.IsOnline = false;
        var api = new BookWormApiClient(_app.AppClient());

        var created = await api.CreateHighlightAsync(bookId, new CreateHighlightRequest { FileId = file.Id, Location = "cfi", Text = "Call me Ishmael", Position = 0.01 });
        var updated = await api.UpdateHighlightAsync(bookId, created.Id, new UpdateHighlightRequest { Color = HighlightColor.Green, Note = "Opening line", Version = created.Version });
        var afterUpdate = await api.GetHighlightsAsync(bookId);
        await api.DeleteHighlightAsync(bookId, created.Id);

        Assert.Equal(BookFormat.Epub, created.Format);
        Assert.Equal("Opening line", updated.Note);
        Assert.Equal(HighlightColor.Green, Assert.Single(afterUpdate).Color);
        Assert.Empty(await api.GetHighlightsAsync(bookId));
        var queued = _app.Queue.ReadAll(OfflineFixture.UserId).Select(entry => entry.Change.Method).ToList();
        Assert.Equal(["POST", "PUT", "DELETE"], queued);
        Assert.Equal(created.Id, JsonSerializer.Deserialize<CreateHighlightRequest>(_app.Queue.ReadAll(OfflineFixture.UserId)[0].Change.Body, BookWormJson.Options).Id);
    }

    [Fact]
    public async Task Offline_FinishingARead_CanAlsoFinishTheBook()
    {
        var (bookId, _) = _app.AddDownloadedBook();
        _app.Connectivity.IsOnline = false;
        var api = new BookWormApiClient(_app.AppClient());
        var read = await api.StartOrResumeReadAsync(bookId);

        var finished = await api.FinishReadAsync(bookId, read.Id, new FinishReadRequest { UpdateBookStatus = true });

        Assert.Equal(ReadStatus.Finished, finished.Status);
        Assert.Null(await api.GetCurrentReadAsync(bookId));
        Assert.Equal(BookStatus.Finished, (await api.GetBookAsync(bookId)).Status);
    }

    [Fact]
    public async Task Online_Answers_KeepTheDownloadUpToDate()
    {
        var (bookId, file) = _app.AddDownloadedBook();
        var fromServer = _app.Store.Read<BookDetails>(OfflineFixture.UserId, bookId, OfflineStore.BookJson) with { Title = "Moby-Dick; or, The Whale" };
        _app.Server.Respond = (_, _) => FakeServer.Json(fromServer);

        var book = await new BookWormApiClient(_app.AppClient()).GetBookAsync(bookId);

        Assert.Equal("Moby-Dick; or, The Whale", book.Title);
        Assert.Equal(2, book.Files.Count);
        Assert.Equal("Moby-Dick; or, The Whale", _app.Store.Read<BookDetails>(OfflineFixture.UserId, bookId, OfflineStore.BookJson).Title);
        Assert.Empty(_app.Queue.ReadAll(OfflineFixture.UserId));
    }

    [Fact]
    public async Task Online_ButTheServerIsUnreachable_ItCarriesOnOffline()
    {
        var read = new ReadDetails(Guid.NewGuid(), Guid.Empty, ReadStatus.CurrentlyReading, DateTimeOffset.UnixEpoch, null, null, null, null, null, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, 1);
        var (bookId, file) = _app.AddDownloadedBook(read);
        _app.Server.Unreachable = true;
        var api = new BookWormApiClient(_app.AppClient());

        await api.SaveProgressAsync(bookId, read.Id, new ReadProgressRequest { FileId = file.Id, Location = "here", Progress = 0.5 });

        Assert.Equal("here", (await api.GetCurrentReadAsync(bookId)).Location);
        Assert.Single(_app.Queue.ReadAll(OfflineFixture.UserId));
    }

    [Fact]
    public async Task Online_SavingProgress_UpdatesTheDownload_WithoutQueueing()
    {
        var read = new ReadDetails(Guid.NewGuid(), Guid.Empty, ReadStatus.CurrentlyReading, DateTimeOffset.UnixEpoch, null, null, null, null, null, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, 1);
        var (bookId, file) = _app.AddDownloadedBook(read);

        await new BookWormApiClient(_app.AppClient()).SaveProgressAsync(bookId, read.Id, new ReadProgressRequest { FileId = file.Id, Location = "p. 40", Progress = 0.4 });

        Assert.Equal("p. 40", _app.Store.Read<ReadDetails>(OfflineFixture.UserId, bookId, OfflineStore.ReadJson).Location);
        Assert.Single(_app.Server.Requests);
        Assert.Empty(_app.Queue.ReadAll(OfflineFixture.UserId));
    }

    [Fact]
    public async Task BrowsePositions_AreKeptOnTheDownloadedFile()
    {
        var (bookId, file) = _app.AddDownloadedBook();
        _app.Connectivity.IsOnline = false;
        var api = new BookWormApiClient(_app.AppClient());

        await api.SaveBrowsePositionAsync(bookId, file.Id, new BrowsePositionRequest { Location = "a", Progress = 0.1 });
        await api.SaveBrowsePositionAsync(bookId, file.Id, new BrowsePositionRequest { Location = "b", Progress = 0.2 });

        Assert.Equal("b", Assert.Single((await api.GetBookAsync(bookId)).Files).BrowseLocation);
        Assert.Single(_app.Queue.ReadAll(OfflineFixture.UserId));
    }
}
