using System.Net;
using BookWorm.Mobile.Core.Offline;
using BookWorm.Mobile.Core.WebView;

namespace BookWorm.Mobile.Core.Tests;

public sealed class WebViewFilesTests : IDisposable
{
    private readonly OfflineFixture _app = new();

    public void Dispose() => _app.Dispose();

    private WebViewFiles Files() => new(_app.Store, _app.Session, _app.Connectivity, () => _app.Server.Client());

    private static Uri App(string path) => new("https://0.0.0.1/" + path);

    [Fact]
    public async Task DownloadedFiles_AreServedFromThePhone_EvenOnline()
    {
        var (bookId, file) = _app.AddDownloadedBook();

        var response = Files().FindDownloaded(App($"api/books/{bookId}/files/{file.Id}"));

        Assert.Equal(200, response.StatusCode);
        Assert.Equal("application/epub+zip", response.ContentType);
        Assert.Equal("epub", await new StreamReader(response.Body).ReadToEndAsync());
        Assert.Empty(_app.Server.Requests);
    }

    [Fact]
    public async Task OtherFiles_AreFetchedFromTheServer_WithTheToken()
    {
        _app.Server.Respond = (_, _) => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("cover bytes") };
        var bookId = Guid.NewGuid();

        var files = Files();
        Assert.Null(files.FindDownloaded(App($"api/books/{bookId}/cover?v=3")));
        var body = await files.FetchAsync(App($"api/books/{bookId}/cover?v=3"), CancellationToken.None);

        Assert.Equal("cover bytes", await new StreamReader(body).ReadToEndAsync());
        Assert.Equal($"api/books/{bookId}/cover?v=3", Assert.Single(_app.Server.Requests).Path);
    }

    [Fact]
    public async Task Offline_FilesThatArentDownloaded_AreUnavailable()
    {
        _app.Connectivity.IsOnline = false;

        var body = await Files().FetchAsync(App($"api/books/{Guid.NewGuid()}/cover"), CancellationToken.None);

        Assert.Same(Stream.Null, body);
        Assert.Empty(_app.Server.Requests);
    }

    [Theory]
    [InlineData("GET", "https://0.0.0.1/api/books/x/cover", true)]
    [InlineData("PUT", "https://0.0.0.1/api/books/x/files/epub", false)]
    [InlineData("GET", "https://0.0.0.1/_content/BookWorm.UI/js/reader.js", false)]
    public void OnlyApiReads_AreHandled(string method, string url, bool handled) =>
        Assert.Equal(handled, WebViewFiles.Handles(method, new Uri(url)));
}
