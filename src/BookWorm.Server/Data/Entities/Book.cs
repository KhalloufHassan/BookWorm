using BookWorm.Contracts;

namespace BookWorm.Server.Data;

/// <summary>A book in one user's private library.</summary>
public sealed class Book : IAuditable
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid UserId { get; set; }

    public required string Title { get; set; }
    public BookStatus Status { get; set; } = BookStatus.WantToRead;

    /// <summary>0.0–5.0 with one decimal.</summary>
    public decimal? Rating { get; set; }

    /// <summary>The owner's notes, in Markdown.</summary>
    public string Notes { get; set; }

    public DateOnly? OriginalPublicationDate { get; set; }

    /// <summary>When the cover image last changed; null when the book has no cover. The image is on disk.</summary>
    public DateTimeOffset? CoverUpdatedAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>Optimistic concurrency token, mapped to PostgreSQL's <c>xmin</c> system column.</summary>
    public uint Version { get; set; }

    /// <summary>The book's authors; <see cref="BookAuthor.Position"/> gives the display order.</summary>
    public List<BookAuthor> Authors { get; } = [];
    public List<Tag> Tags { get; } = [];
    public List<CollectionBook> Collections { get; } = [];
    public List<Read> Reads { get; } = [];
    public List<BookFile> Files { get; } = [];
    public List<Highlight> Highlights { get; } = [];
}
