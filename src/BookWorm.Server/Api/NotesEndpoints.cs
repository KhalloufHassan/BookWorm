using System.IO.Compression;
using System.Text;
using BookWorm.Server.Data;
using BookWorm.Server.Notes;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace BookWorm.Server.Api;

/// <summary>Your notes and highlights as Markdown downloads (the same files as the automatic export).</summary>
internal static class NotesEndpoints
{
    public static void MapNotesEndpoints(this IEndpointRouteBuilder api)
    {
        api.MapGet("/books/{id:guid}/notes.md", DownloadBookNotes).WithTags("Notes")
            .WithSummary("A book's notes and highlights as one Markdown file.");
        api.MapGet("/notes/export.zip", DownloadAllNotes).WithTags("Notes")
            .WithSummary("A zip of Markdown files: one per book with notes or highlights.");
    }

    private static async Task<Results<FileContentHttpResult, NotFound>> DownloadBookNotes(
        Guid id, AppDbContext db, CancellationToken cancellationToken)
    {
        var book = (await NotesMarkdown.LoadAsync(db.Books.Where(b => b.Id == id), cancellationToken)).SingleOrDefault();
        if (book is null)
        {
            return TypedResults.NotFound();
        }

        var name = NotesMarkdown.FileNames([book])[book.Id];
        return TypedResults.File(Encoding.UTF8.GetBytes(NotesMarkdown.Render(book)), "text/markdown; charset=utf-8", name);
    }

    private static async Task<FileContentHttpResult> DownloadAllNotes(AppDbContext db, TimeProvider timeProvider, CancellationToken cancellationToken)
    {
        var books = await NotesMarkdown.LoadAsync(
            db.Books.Where(b => b.Notes != null || b.Highlights.Any()), cancellationToken);
        var names = NotesMarkdown.FileNames(books);

        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var book in books)
            {
                var entry = zip.CreateEntry(names[book.Id], CompressionLevel.Optimal);
                await using var stream = await entry.OpenAsync(cancellationToken);
                await stream.WriteAsync(Encoding.UTF8.GetBytes(NotesMarkdown.Render(book)), cancellationToken);
            }
        }

        var date = timeProvider.GetUtcNow().ToString("yyyy-MM-dd");
        return TypedResults.File(buffer.ToArray(), "application/zip", $"bookworm-notes-{date}.zip");
    }
}
