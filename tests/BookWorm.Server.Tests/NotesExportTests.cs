using System.IO.Compression;
using BookWorm.Contracts;
using BookWorm.Server.Notes;
using BookWorm.Server.Tests.Infrastructure;
using BookWorm.UI.Api;

namespace BookWorm.Server.Tests;

public sealed class NotesExportTests(BookWormAppFactory app)
{
    [Fact]
    public async Task ABooksNotes_DownloadAsMarkdown()
    {
        var user = await app.CreateUserAsync();
        var api = user.Api;
        var author = await api.AddAuthorAsync("Susanna Clarke");
        var tag = await api.AddTagAsync("fantasy");
        var book = await api.AddBookAsync("Piranesi", [author.Id], [tag.Id], BookStatus.Finished, 5m, new DateOnly(2020, 9, 15), "A **beautiful** house.");
        var file = await api.UploadSampleAsync(book.Id, BookFormat.Epub);
        await api.CreateHighlightAsync(book.Id, new CreateHighlightRequest
        {
            FileId = file.Id,
            Location = "epubcfi(/6/2!/4/2/1:0)",
            Text = "The Beauty of the House is immeasurable;\nits Kindness infinite.",
            Chapter = "Part 1",
            PageLabel = "5",
            Position = 0.01,
            Note = "Opening line.",
        });

        using var response = await user.Http.GetAsync(BookWormApiClient.NotesUrl(book.Id));
        response.EnsureSuccessStatusCode();
        Assert.Equal("text/markdown", response.Content.Headers.ContentType?.MediaType);
        var markdown = await response.Content.ReadAsStringAsync();

        Assert.StartsWith("---\ntitle: \"Piranesi\"\nauthors:\n  - \"Susanna Clarke\"\nstatus: Finished\nrating: 5.0\npublished: 2020-09-15\ntags:\n  - \"fantasy\"\n", markdown);
        Assert.Contains($"{NotesMarkdown.IdKey}: {book.Id}\n---\n\n# Piranesi\n\n*by Susanna Clarke*\n\n## Notes\n\nA **beautiful** house.\n", markdown);
        Assert.Contains("## Highlights\n\n> The Beauty of the House is immeasurable;\n> its Kindness infinite.\n\n*Part 1 · p. 5*\n\nOpening line.\n", markdown);
    }

    [Fact]
    public async Task AllNotes_DownloadAsAZip_OfBooksThatHaveAny()
    {
        var user = await app.CreateUserAsync();
        var api = user.Api;
        var author = await api.AddAuthorAsync("Jane Austen");
        await api.AddBookAsync("Emma", [author.Id], notes: "Meddling.");
        await api.AddBookAsync("Persuasion", [author.Id], notes: "Second chances.");
        await api.AddBookAsync("Sanditon", [author.Id]);

        using var response = await user.Http.GetAsync(BookWormApiClient.NotesExportUrl);
        response.EnsureSuccessStatusCode();
        using var zip = new ZipArchive(await response.Content.ReadAsStreamAsync());

        Assert.Equal(["Jane Austen - Emma.md", "Jane Austen - Persuasion.md"], zip.Entries.Select(e => e.FullName).Order());
    }

    [Fact]
    public async Task TheNotesFolder_FollowsChanges()
    {
        var user = await app.CreateUserAsync();
        var book = await user.Api.AddBookAsync("Emma", notes: "Meddling.");
        var folder = Path.Combine(app.DataFolder, "data", "notes", user.UserName);
        var first = Path.Combine(folder, "Emma.md");

        await WaitUntilAsync(() => File.Exists(first));
        Assert.Contains("Meddling.", await File.ReadAllTextAsync(first));

        // Files you add yourself are left alone.
        var mine = Path.Combine(folder, "my own notes.md");
        await File.WriteAllTextAsync(mine, "# Mine\n");

        var update = book.ToUpdate();
        update.Title = "Emma (annotated)";
        await user.Api.UpdateBookAsync(book.Id, update);
        var renamed = Path.Combine(folder, "Emma (annotated).md");
        await WaitUntilAsync(() => File.Exists(renamed) && !File.Exists(first));

        await user.Api.DeleteBookAsync(book.Id);
        await WaitUntilAsync(() => !File.Exists(renamed));
        Assert.True(File.Exists(mine));
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++)
        {
            await Task.Delay(100);
        }

        Assert.True(condition(), "The notes folder didn't catch up in time.");
    }
}
