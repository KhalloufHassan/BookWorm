using System.Net;
using System.Net.Http.Headers;
using BookWorm.Contracts;
using BookWorm.Server.Tests.Infrastructure;
using BookWorm.UI.Api;

namespace BookWorm.Server.Tests;

public sealed class FilesApiTests(BookWormAppFactory app)
{
    [Theory]
    [InlineData(BookFormat.Epub)]
    [InlineData(BookFormat.Pdf)]
    [InlineData(BookFormat.Mobi)]
    [InlineData(BookFormat.Azw3)]
    [InlineData(BookFormat.Fb2)]
    [InlineData(BookFormat.Cbz)]
    public async Task EveryFormat_CanBeUploadedAndDownloaded(BookFormat format)
    {
        var user = await app.CreateUserAsync();
        var book = await user.Api.AddBookAsync("The Hobbit");

        var file = await user.Api.UploadSampleAsync(book.Id, format);

        Assert.Equal(format, file.Format);
        Assert.Equal(64, file.Sha256.Length);
        using var response = await user.Http.GetAsync(BookWormApiClient.FileUrl(book.Id, file.Id));
        response.EnsureSuccessStatusCode();
        Assert.Equal(SampleFiles.For(format), await response.Content.ReadAsByteArrayAsync());
        Assert.Equal(BookFormats.ContentType(format), response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("sandbox", response.Headers.GetValues("Content-Security-Policy").Single());

        var details = await user.Api.GetBookAsync(book.Id);
        Assert.Equal(file, Assert.Single(details.Files));
        var summary = Assert.Single((await user.Api.GetBooksAsync()).Items);
        Assert.Equal([format], summary.Formats);
    }

    [Fact]
    public async Task Downloads_SupportRangesAndTheOriginalFileName()
    {
        var user = await app.CreateUserAsync();
        var book = await user.Api.AddBookAsync("Emma");
        var file = await user.Api.UploadSampleAsync(book.Id, BookFormat.Pdf);

        using var request = new HttpRequestMessage(HttpMethod.Get, BookWormApiClient.FileUrl(book.Id, file.Id));
        request.Headers.Range = new RangeHeaderValue(0, 7);
        using var partial = await user.Http.SendAsync(request);
        Assert.Equal(HttpStatusCode.PartialContent, partial.StatusCode);
        Assert.Equal("%PDF-1.4"u8.ToArray(), await partial.Content.ReadAsByteArrayAsync());

        using var download = await user.Http.GetAsync(BookWormApiClient.FileUrl(book.Id, file.Id, download: true));
        Assert.Equal("sample.pdf", download.Content.Headers.ContentDisposition?.FileNameStar ?? download.Content.Headers.ContentDisposition?.FileName);
    }

    [Fact]
    public async Task UploadingTheSameFormatAgain_ReplacesTheFile()
    {
        var user = await app.CreateUserAsync();
        var book = await user.Api.AddBookAsync("Persuasion");
        var first = await user.Api.UploadSampleAsync(book.Id, BookFormat.Epub, "First edition.");

        var second = await user.Api.UploadSampleAsync(book.Id, BookFormat.Epub, "Second edition, corrected.");

        Assert.Equal(first.Id, second.Id);
        Assert.NotEqual(first.Sha256, second.Sha256);
        Assert.Single((await user.Api.GetBookAsync(book.Id)).Files);
    }

    [Fact]
    public async Task Files_MustMatchTheirFormat()
    {
        var user = await app.CreateUserAsync();
        var book = await user.Api.AddBookAsync("Middlemarch");

        var error = await Assert.ThrowsAsync<ApiException>(() =>
            user.Api.UploadFileAsync(book.Id, BookFormat.Epub, new MemoryStream(SampleFiles.Pdf("not a zip")), "fake.epub"));

        Assert.Equal(HttpStatusCode.BadRequest, error.StatusCode);
        Assert.Contains("valid EPUB", Assert.Single(error.Errors["file"]));
    }

    [Fact]
    public async Task UnknownFormats_AreRejected()
    {
        var user = await app.CreateUserAsync();
        var book = await user.Api.AddBookAsync("Middlemarch");

        using var response = await user.Http.PutAsync($"api/books/{book.Id}/files/docx", new ByteArrayContent([1, 2, 3]));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task DeletingAFile_RemovesItFromTheBookAndTheDisk()
    {
        var user = await app.CreateUserAsync();
        var book = await user.Api.AddBookAsync("Dracula");
        var file = await user.Api.UploadSampleAsync(book.Id, BookFormat.Epub);
        var path = FilePath(user, book.Id, file.Id);
        Assert.True(File.Exists(path));

        await user.Api.DeleteFileAsync(book.Id, file.Id);

        Assert.Empty((await user.Api.GetBookAsync(book.Id)).Files);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task DeletingABook_RemovesItsFilesAndCover()
    {
        var user = await app.CreateUserAsync();
        var book = await user.Api.AddBookAsync("Frankenstein");
        var file = await user.Api.UploadSampleAsync(book.Id, BookFormat.Pdf);
        await user.Api.UploadCoverAsync(book.Id, new MemoryStream(SampleFiles.Png), "image/png");

        await user.Api.DeleteBookAsync(book.Id);

        Assert.False(File.Exists(FilePath(user, book.Id, file.Id)));
        Assert.False(File.Exists(Path.Combine(app.DataFolder, "data", "covers", user.Id.ToString(), book.Id.ToString())));
    }

    [Fact]
    public async Task Covers_CanBeUploadedShownAndRemoved()
    {
        var user = await app.CreateUserAsync();
        var book = await user.Api.AddBookAsync("Rebecca");
        Assert.Null(book.CoverVersion);

        var cover = await user.Api.UploadCoverAsync(book.Id, new MemoryStream(SampleFiles.Png), "image/png");

        var details = await user.Api.GetBookAsync(book.Id);
        Assert.Equal(cover.CoverVersion, details.CoverVersion);
        Assert.Equal(cover.CoverVersion, Assert.Single((await user.Api.GetBooksAsync()).Items).CoverVersion);
        using (var image = await user.Http.GetAsync(BookWormApiClient.CoverUrl(book.Id, cover.CoverVersion)))
        {
            image.EnsureSuccessStatusCode();
            Assert.Equal("image/png", image.Content.Headers.ContentType?.MediaType);
            Assert.Contains("immutable", image.Headers.CacheControl?.ToString());
            Assert.Equal(SampleFiles.Png, await image.Content.ReadAsByteArrayAsync());
        }

        await user.Api.DeleteCoverAsync(book.Id);

        Assert.Null((await user.Api.GetBookAsync(book.Id)).CoverVersion);
        using var missing = await user.Http.GetAsync($"api/books/{book.Id}/cover");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task Covers_MustBeImages()
    {
        var user = await app.CreateUserAsync();
        var book = await user.Api.AddBookAsync("Rebecca");

        var error = await Assert.ThrowsAsync<ApiException>(() =>
            user.Api.UploadCoverAsync(book.Id, new MemoryStream("<svg onload='alert(1)'/>"u8.ToArray()), "image/svg+xml"));

        Assert.Equal(HttpStatusCode.BadRequest, error.StatusCode);
    }

    [Fact]
    public async Task Covers_OverTheLimit_AreRejected()
    {
        var user = await app.CreateUserAsync();
        var book = await user.Api.AddBookAsync("Rebecca");
        var huge = new byte[ApiLimits.CoverMaxBytes + 1];
        SampleFiles.Png.CopyTo(huge, 0);

        var error = await Assert.ThrowsAsync<ApiException>(() => user.Api.UploadCoverAsync(book.Id, new MemoryStream(huge), "image/png"));

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, error.StatusCode);
    }

    [Fact]
    public async Task OtherUsers_CannotReachYourFilesOrCovers()
    {
        var owner = await app.CreateUserAsync();
        var stranger = await app.CreateUserAsync();
        var book = await owner.Api.AddBookAsync("Private diary");
        var file = await owner.Api.UploadSampleAsync(book.Id, BookFormat.Epub);
        var cover = await owner.Api.UploadCoverAsync(book.Id, new MemoryStream(SampleFiles.Png), "image/png");

        using var download = await stranger.Http.GetAsync(BookWormApiClient.FileUrl(book.Id, file.Id));
        using var image = await stranger.Http.GetAsync(BookWormApiClient.CoverUrl(book.Id, cover.CoverVersion));
        var upload = await Assert.ThrowsAsync<ApiException>(() => stranger.Api.UploadSampleAsync(book.Id, BookFormat.Pdf));
        var delete = await Assert.ThrowsAsync<ApiException>(() => stranger.Api.DeleteFileAsync(book.Id, file.Id));

        Assert.Equal(HttpStatusCode.NotFound, download.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, image.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, upload.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, delete.StatusCode);
        Assert.Single((await owner.Api.GetBookAsync(book.Id)).Files);
    }

    private string FilePath(TestUser user, Guid bookId, Guid fileId) =>
        Path.Combine(app.DataFolder, "data", "books", user.Id.ToString(), bookId.ToString(), fileId.ToString());
}
