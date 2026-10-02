using System.Net;
using BookWorm.Contracts;
using BookWorm.Server.Tests.Infrastructure;
using BookWorm.UI.Api;

namespace BookWorm.Server.Tests;

public sealed class HighlightsApiTests(BookWormAppFactory app)
{
    [Fact]
    public async Task Highlights_CanBeCreatedEditedAndDeleted()
    {
        var (api, book, file) = await BookWithFileAsync();

        var created = await api.CreateHighlightAsync(book.Id, Highlight(file.Id, "stormy night", 0.4));
        var later = await api.CreateHighlightAsync(book.Id, Highlight(file.Id, "a dark", 0.1));

        Assert.Equal(HighlightState.Anchored, created.State);
        Assert.Equal(BookFormat.Epub, created.Format);
        Assert.Equal(["a dark", "stormy night"], (await api.GetHighlightsAsync(book.Id)).Select(h => h.Text));
        Assert.Equal(2, (await api.GetBookAsync(book.Id)).HighlightCount);

        var edited = await api.UpdateHighlightAsync(book.Id, created.Id, new UpdateHighlightRequest
        {
            Color = HighlightColor.Blue,
            Note = "Such a *cliché*.",
            Version = created.Version,
        });
        Assert.Equal(HighlightColor.Blue, edited.Color);
        Assert.Equal("Such a *cliché*.", edited.Note);

        await api.DeleteHighlightAsync(book.Id, later.Id);
        Assert.Equal([created.Id], (await api.GetHighlightsAsync(book.Id)).Select(h => h.Id));
    }

    [Fact]
    public async Task EditingAStaleHighlight_IsAConflict()
    {
        var (api, book, file) = await BookWithFileAsync();
        var highlight = await api.CreateHighlightAsync(book.Id, Highlight(file.Id, "dark"));
        await api.UpdateHighlightAsync(book.Id, highlight.Id, new UpdateHighlightRequest { Note = "first", Version = highlight.Version });

        var error = await Assert.ThrowsAsync<ApiException>(() => api.UpdateHighlightAsync(
            book.Id, highlight.Id, new UpdateHighlightRequest { Note = "second", Version = highlight.Version }));

        Assert.Equal(HttpStatusCode.Conflict, error.StatusCode);
    }

    [Fact]
    public async Task ReplacingTheFile_AsksTheReaderToFindHighlightsAgain()
    {
        var (api, book, file) = await BookWithFileAsync();
        var kept = await api.CreateHighlightAsync(book.Id, Highlight(file.Id, "dark"));
        var lost = await api.CreateHighlightAsync(book.Id, Highlight(file.Id, "stormy"));

        var replaced = await api.UploadSampleAsync(book.Id, BookFormat.Epub, "It was a dark and quiet night.");

        Assert.All(await api.GetHighlightsAsync(book.Id), h => Assert.Equal(HighlightState.NeedsCheck, h.State));

        var found = await api.ReanchorHighlightAsync(book.Id, kept.Id, new ReanchorHighlightRequest
        {
            FileId = replaced.Id,
            Found = true,
            Location = "epubcfi(/6/2!/4/2/1:8)",
            Position = 0.2,
        });
        var missing = await api.ReanchorHighlightAsync(book.Id, lost.Id, new ReanchorHighlightRequest { FileId = replaced.Id, Found = false });

        Assert.Equal(HighlightState.Anchored, found.State);
        Assert.Equal("epubcfi(/6/2!/4/2/1:8)", found.Location);
        Assert.Equal(HighlightState.Missing, missing.State);
        Assert.Equal("stormy", missing.Text);
    }

    [Fact]
    public async Task DeletingTheFile_KeepsHighlights_AndANewFileInTheSameFormatBringsThemBack()
    {
        var (api, book, file) = await BookWithFileAsync();
        var highlight = await api.CreateHighlightAsync(book.Id, Highlight(file.Id, "dark", note: "Keep this thought."));

        await api.DeleteFileAsync(book.Id, file.Id);

        var orphan = Assert.Single(await api.GetHighlightsAsync(book.Id));
        Assert.Null(orphan.FileId);
        Assert.Equal(HighlightState.Missing, orphan.State);
        Assert.Equal("Keep this thought.", orphan.Note);

        var pdf = await api.UploadSampleAsync(book.Id, BookFormat.Pdf);
        Assert.Null(Assert.Single(await api.GetHighlightsAsync(book.Id)).FileId);

        var epub = await api.UploadSampleAsync(book.Id, BookFormat.Epub, "A new edition: it was a dark and stormy night.");
        var back = Assert.Single(await api.GetHighlightsAsync(book.Id));
        Assert.Equal(epub.Id, back.FileId);
        Assert.Equal(HighlightState.NeedsCheck, back.State);
        Assert.NotEqual(pdf.Id, back.FileId);
        Assert.Equal(highlight.Id, back.Id);
    }

    [Fact]
    public async Task Highlights_CannotMoveToAFileInAnotherFormat()
    {
        var (api, book, file) = await BookWithFileAsync();
        var highlight = await api.CreateHighlightAsync(book.Id, Highlight(file.Id, "dark"));
        var pdf = await api.UploadSampleAsync(book.Id, BookFormat.Pdf);

        var error = await Assert.ThrowsAsync<ApiException>(() => api.ReanchorHighlightAsync(
            book.Id, highlight.Id, new ReanchorHighlightRequest { FileId = pdf.Id, Found = true, Location = "page:1" }));

        Assert.Equal(HttpStatusCode.BadRequest, error.StatusCode);
    }

    [Fact]
    public async Task OtherUsers_CannotSeeOrChangeYourHighlights()
    {
        var (api, book, file) = await BookWithFileAsync();
        var highlight = await api.CreateHighlightAsync(book.Id, Highlight(file.Id, "dark"));
        var stranger = (await app.CreateUserAsync()).Api;

        var list = await Assert.ThrowsAsync<ApiException>(() => stranger.GetHighlightsAsync(book.Id));
        var create = await Assert.ThrowsAsync<ApiException>(() => stranger.CreateHighlightAsync(book.Id, Highlight(file.Id, "mine")));
        var delete = await Assert.ThrowsAsync<ApiException>(() => stranger.DeleteHighlightAsync(book.Id, highlight.Id));

        Assert.Equal(HttpStatusCode.NotFound, list.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, create.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, delete.StatusCode);
        Assert.Single(await api.GetHighlightsAsync(book.Id));
    }

    private async Task<(BookWormApiClient Api, BookDetails Book, BookFileDetails File)> BookWithFileAsync()
    {
        var api = (await app.CreateUserAsync()).Api;
        var book = await api.AddBookAsync("A Wrinkle in Time");
        var file = await api.UploadSampleAsync(book.Id, BookFormat.Epub);
        return (api, book, file);
    }

    [Fact]
    public async Task CreatingAHighlight_WithAnIdAgain_ReturnsTheSameOne()
    {
        var api = (await app.CreateUserAsync()).Api;
        var book = await api.AddBookAsync("Moby-Dick");
        var file = await api.UploadSampleAsync(book.Id, BookFormat.Epub);
        var request = Highlight(file.Id, "stormy night");
        request.Id = Guid.NewGuid();

        var first = await api.CreateHighlightAsync(book.Id, request);
        var retry = await api.CreateHighlightAsync(book.Id, request);

        Assert.Equal(request.Id, first.Id);
        Assert.Equal(first.Id, retry.Id);
        Assert.Single(await api.GetHighlightsAsync(book.Id));
    }

    [Fact]
    public async Task CreatingAHighlight_WithAnotherUsersId_Conflicts()
    {
        var owner = (await app.CreateUserAsync()).Api;
        var ownerBook = await owner.AddBookAsync("Moby-Dick");
        var ownerFile = await owner.UploadSampleAsync(ownerBook.Id, BookFormat.Epub);
        var taken = await owner.CreateHighlightAsync(ownerBook.Id, Highlight(ownerFile.Id, "stormy night"));
        var stranger = (await app.CreateUserAsync()).Api;
        var book = await stranger.AddBookAsync("Emma");
        var file = await stranger.UploadSampleAsync(book.Id, BookFormat.Epub);
        var request = Highlight(file.Id, "dark");
        request.Id = taken.Id;

        var error = await Assert.ThrowsAsync<ApiException>(() => stranger.CreateHighlightAsync(book.Id, request));

        Assert.Equal(HttpStatusCode.Conflict, error.StatusCode);
        Assert.Equal("stormy night", Assert.Single(await owner.GetHighlightsAsync(ownerBook.Id)).Text);
    }

    private static CreateHighlightRequest Highlight(Guid fileId, string text, double position = 0.5, string note = null) => new()
    {
        FileId = fileId,
        Location = "epubcfi(/6/2!/4/2/1:0)",
        Text = text,
        Prefix = "It was a ",
        Suffix = " and",
        Chapter = "One",
        Position = position,
        Color = HighlightColor.Yellow,
        Note = note,
    };
}
