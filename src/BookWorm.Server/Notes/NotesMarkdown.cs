using System.Globalization;
using System.Text;
using BookWorm.Contracts;
using BookWorm.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace BookWorm.Server.Notes;

/// <summary>A book's notes and highlights, as exported to Markdown.</summary>
public sealed record NotesBook(
    Guid Id,
    string Title,
    BookStatus Status,
    decimal? Rating,
    DateOnly? Published,
    List<string> Authors,
    List<string> Tags,
    string Notes,
    List<NotesHighlight> Highlights);

public sealed record NotesHighlight(
    string Text,
    string Note,
    string Chapter,
    string PageLabel,
    HighlightColor Color,
    bool Missing);

/// <summary>
/// Renders a book's notes and highlights as one Markdown file with YAML front matter, readable in
/// any Markdown editor (and friendly to Obsidian and similar note apps).
/// </summary>
public static class NotesMarkdown
{
    /// <summary>Front matter key that marks files BookWorm manages (and may therefore rewrite or delete).</summary>
    public const string IdKey = "bookworm_id";

    private const int MaxFileNameLength = 150;

    /// <summary>Books of <paramref name="books"/> as export models, notes and highlights included.</summary>
    public static Task<List<NotesBook>> LoadAsync(IQueryable<Book> books, CancellationToken cancellationToken) =>
        books
            .OrderBy(b => b.Id)
            .Select(b => new NotesBook(
                b.Id,
                b.Title,
                b.Status,
                b.Rating,
                b.OriginalPublicationDate,
                b.Authors.OrderBy(ba => ba.Position).Select(ba => ba.Author.Name).ToList(),
                b.Tags.OrderBy(t => t.Name).Select(t => t.Name).ToList(),
                b.Notes,
                b.Highlights
                    .OrderBy(h => h.Position).ThenBy(h => h.CreatedAt)
                    .Select(h => new NotesHighlight(h.Text, h.Note, h.Chapter, h.PageLabel, h.Color, h.FileId == null || h.IsMissing))
                    .ToList()))
            .AsSplitQuery()
            .ToListAsync(cancellationToken);

    public static string Render(NotesBook book)
    {
        var md = new StringBuilder();
        md.Append("---\n");
        md.Append("title: ").Append(Quote(book.Title)).Append('\n');
        AppendList(md, "authors", book.Authors);
        md.Append("status: ").Append(book.Status).Append('\n');
        if (book.Rating is { } rating)
        {
            md.Append("rating: ").Append(rating.ToString("0.0", CultureInfo.InvariantCulture)).Append('\n');
        }

        if (book.Published is { } published)
        {
            md.Append("published: ").Append(published.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).Append('\n');
        }

        AppendList(md, "tags", book.Tags);
        md.Append(IdKey).Append(": ").Append(book.Id).Append('\n');
        md.Append("---\n\n");

        md.Append("# ").Append(book.Title.ReplaceLineEndings(" ")).Append("\n\n");
        if (book.Authors.Count > 0)
        {
            md.Append("*by ").Append(string.Join(", ", book.Authors)).Append("*\n\n");
        }

        if (!string.IsNullOrWhiteSpace(book.Notes))
        {
            md.Append("## Notes\n\n").Append(book.Notes.Trim().ReplaceLineEndings("\n")).Append("\n\n");
        }

        if (book.Highlights.Count > 0)
        {
            md.Append("## Highlights\n");
            foreach (var highlight in book.Highlights)
            {
                md.Append('\n');
                foreach (var line in highlight.Text.Trim().ReplaceLineEndings("\n").Split('\n'))
                {
                    md.Append("> ").Append(line).Append('\n');
                }

                var where = string.Join(" · ", new[]
                {
                    highlight.Chapter,
                    highlight.PageLabel is { Length: > 0 } page ? $"p. {page}" : null,
                    highlight.Missing ? "not found in the current file" : null,
                }.Where(part => !string.IsNullOrWhiteSpace(part)));
                if (where.Length > 0)
                {
                    md.Append('\n').Append('*').Append(where.ReplaceLineEndings(" ")).Append("*\n");
                }

                if (!string.IsNullOrWhiteSpace(highlight.Note))
                {
                    md.Append('\n').Append(highlight.Note.Trim().ReplaceLineEndings("\n")).Append('\n');
                }
            }
        }

        return md.ToString().TrimEnd('\n') + "\n";
    }

    /// <summary>"Author - Title.md", made safe for every file system; clashes get " (2)", " (3)"…</summary>
    public static Dictionary<Guid, string> FileNames(IEnumerable<NotesBook> books)
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var names = new Dictionary<Guid, string>();
        foreach (var book in books)
        {
            var baseName = SafeName(book.Authors.Count > 0 ? $"{book.Authors[0]} - {book.Title}" : book.Title, "Untitled");
            var name = baseName + ".md";
            for (var n = 2; !used.Add(name); n++)
            {
                name = $"{baseName} ({n}).md";
            }

            names[book.Id] = name;
        }

        return names;
    }

    /// <summary>A single path segment made from free text: no separators, reserved characters or leading dots.</summary>
    public static string SafeName(string text, string fallback)
    {
        var cleaned = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            cleaned.Append(char.IsControl(c) || c is '/' or '\\' or ':' or '*' or '?' or '"' or '<' or '>' or '|' ? ' ' : c);
        }

        var result = string.Join(' ', cleaned.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries)).Trim('.', ' ');
        if (result.Length > MaxFileNameLength)
        {
            result = result[..MaxFileNameLength].TrimEnd('.', ' ');
        }

        return result.Length == 0 ? fallback : result;
    }

    /// <summary>Whether a Markdown file was written by BookWorm, judging by its front matter.</summary>
    public static bool IsManaged(string content)
    {
        if (!content.StartsWith("---\n", StringComparison.Ordinal))
        {
            return false;
        }

        var end = content.IndexOf("\n---\n", 3, StringComparison.Ordinal);
        return end > 0 && content.AsSpan(0, end).Contains($"\n{IdKey}: ", StringComparison.Ordinal);
    }

    private static void AppendList(StringBuilder md, string key, List<string> values)
    {
        if (values.Count == 0)
        {
            return;
        }

        md.Append(key).Append(":\n");
        foreach (var value in values)
        {
            md.Append("  - ").Append(Quote(value)).Append('\n');
        }
    }

    private static string Quote(string value) =>
        "\"" + value.ReplaceLineEndings(" ").Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
}
