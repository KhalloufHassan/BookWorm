using BookWorm.Contracts;

namespace BookWorm.Server.Data;

/// <summary>A highlighted passage in a book file, with an optional note.</summary>
public sealed class Highlight : IAuditable
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid BookId { get; set; }

    /// <summary>Null once the file is deleted; the highlight is kept and reattached to a new file of the same format.</summary>
    public Guid? FileId { get; set; }
    public BookFormat Format { get; set; }

    /// <summary>The <see cref="BookFile.Sha256"/> of the file version <see cref="Location"/> refers to.</summary>
    public required string AnchoredSha256 { get; set; }

    /// <summary>True when the text couldn't be found in the file version <see cref="AnchoredSha256"/>.</summary>
    public bool IsMissing { get; set; }

    public required string Location { get; set; }
    public required string Text { get; set; }
    public string Prefix { get; set; }
    public string Suffix { get; set; }
    public string Chapter { get; set; }
    public string PageLabel { get; set; }
    public double Position { get; set; }
    public HighlightColor Color { get; set; }

    /// <summary>Markdown.</summary>
    public string Note { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    /// <inheritdoc cref="Book.Version" />
    public uint Version { get; set; }

    public Book Book { get; set; }
    public BookFile File { get; set; }
}
