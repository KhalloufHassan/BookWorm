using System.Net;
using System.Security.Cryptography;
using System.Text;
using BookWorm.Contracts;
using BookWorm.Mobile.Core.Offline;
using BookWorm.Mobile.Core.Updates;

namespace BookWorm.Mobile.Core.Tests;

public sealed class DownloadAndUpdateTests : IDisposable
{
    private readonly OfflineFixture _app = new();

    public void Dispose() => _app.Dispose();

    [Fact]
    public async Task Downloading_StoresTheFileCoverAndDetails()
    {
        var content = Encoding.UTF8.GetBytes("the whole book");
        var (book, file) = ServeBook(content, sha256: Convert.ToHexStringLower(SHA256.HashData(content)));
        var library = Library();
        var reported = new List<double>();

        await library.DownloadAsync(book.Id, new SyncProgress(reported), CancellationToken.None);

        Assert.Equal(content, File.ReadAllBytes(_app.Store.BookFilePath(OfflineFixture.UserId, book.Id, BookFormat.Epub)));
        Assert.NotNull(_app.Store.FindCover(OfflineFixture.UserId, book.Id));
        Assert.Equal(file.Id, _app.Store.Read<DownloadRecord>(OfflineFixture.UserId, book.Id, OfflineStore.FileJson).File.Id);
        var download = await library.GetDownloadAsync(book.Id);
        Assert.Equal("Moby-Dick", download.Title);
        Assert.False(download.UpdateAvailable);
        Assert.Equal(1, reported[^1]);
    }

    [Fact]
    public async Task ADamagedDownload_IsThrownAway()
    {
        var (book, _) = ServeBook(Encoding.UTF8.GetBytes("the whole book"), sha256: new string('0', 64));

        await Assert.ThrowsAsync<IOException>(() => Library().DownloadAsync(book.Id, null, CancellationToken.None));

        Assert.False(_app.Store.IsDownloaded(OfflineFixture.UserId, book.Id));
        Assert.False(Directory.Exists(_app.Store.BookFolder(OfflineFixture.UserId, book.Id)));
    }

    [Fact]
    public async Task RemovingADownload_AlsoDropsItsQueuedChanges()
    {
        var (bookId, _) = _app.AddDownloadedBook();
        _app.Queue.Enqueue(OfflineFixture.UserId, bookId, HttpMethod.Delete, $"api/books/{bookId}/highlights/{Guid.NewGuid()}", null);

        await Library().RemoveAsync(bookId);

        Assert.False(_app.Store.IsDownloaded(OfflineFixture.UserId, bookId));
        Assert.Empty(_app.Queue.ReadAll(OfflineFixture.UserId));
    }

    [Fact]
    public async Task AFileReplacedOnTheServer_OffersAnUpdate()
    {
        var (bookId, file) = _app.AddDownloadedBook();
        _app.Store.Update<BookDetails>(OfflineFixture.UserId, bookId, OfflineStore.BookJson, book => book with { Files = [file with { Sha256 = "changed" }] });

        Assert.True((await Library().GetDownloadAsync(bookId)).UpdateAvailable);
    }

    [Fact]
    public void Updates_PickTheNewestAppRelease()
    {
        var releases = new GitHubReleases(new HttpClient(), "owner/repo");
        List<GitHubReleases.Release> list =
        [
            new("server-v9.0.0", "https://github.com/server", false, false),
            new("android-v1.3.0", "https://github.com/1.3.0", false, true),
            new("android-v1.2.0", "https://github.com/1.2.0", false, false),
            new("android-v1.1.0", "https://github.com/1.1.0", false, false),
        ];

        Assert.Equal(new BookWorm.UI.Services.AppRelease("1.2.0", "https://github.com/1.2.0"), releases.Newest(list, "1.1.0"));
        Assert.Null(releases.Newest(list, "1.2.0"));
    }

    private OfflineLibrary Library()
    {
        var sync = new SyncService(_app.Store, _app.Queue, _app.Session, _app.Connectivity, () => _app.Server.Client());
        return new OfflineLibrary(_app.Store, _app.Queue, _app.Session, _app.Connectivity, sync, () => _app.Server.Client(), _app.Time);
    }

    private (BookDetails Book, BookFileDetails File) ServeBook(byte[] content, string sha256)
    {
        var file = new BookFileDetails(Guid.NewGuid(), BookFormat.Epub, "moby.epub", content.Length, sha256, DateTimeOffset.UnixEpoch, null, null);
        var book = new BookDetails(Guid.NewGuid(), "Moby-Dick", BookStatus.WantToRead, null, null, null, [], [], [], [], [file], 3, 0,
            DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, 1);
        _app.Server.Respond = (request, _) => request.RequestUri.AbsolutePath switch
        {
            var path when path.EndsWith($"/files/{file.Id}") => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(content) },
            var path when path.EndsWith("/cover") => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([0xFF, 0xD8, 0xFF]) },
            var path when path.EndsWith("/highlights") => FakeServer.Json(new List<HighlightDetails>()),
            var path when path.EndsWith("/reads/current") => new HttpResponseMessage(HttpStatusCode.NoContent),
            _ => FakeServer.Json(book),
        };
        return (book, file);
    }

    /// <summary>Records progress synchronously (Progress&lt;T&gt; would post to a thread pool).</summary>
    private sealed class SyncProgress(List<double> values) : IProgress<double>
    {
        public void Report(double value) => values.Add(value);
    }
}
