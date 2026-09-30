using System.ComponentModel.DataAnnotations;

namespace BookWorm.Contracts;

public sealed record AuthorRef(Guid Id, string Name);

public sealed record TagRef(Guid Id, string Name);

/// <param name="CoverVersion">
/// Changes whenever the cover does; null when the book has no cover. The cover image is at
/// <c>GET /api/books/{id}/cover?v={CoverVersion}</c>.
/// </param>
/// <param name="Formats">The formats of the book's files.</param>
/// <param name="Progress">How far into the read in progress, from 0 to 1; null when not reading it in BookWorm.</param>
public sealed record BookSummary(
    Guid Id,
    string Title,
    BookStatus Status,
    decimal? Rating,
    DateOnly? OriginalPublicationDate,
    IReadOnlyList<AuthorRef> Authors,
    IReadOnlyList<TagRef> Tags,
    long? CoverVersion,
    IReadOnlyList<BookFormat> Formats,
    double? Progress,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <param name="CoverVersion">See <see cref="BookSummary.CoverVersion"/>.</param>
public sealed record BookDetails(
    Guid Id,
    string Title,
    BookStatus Status,
    decimal? Rating,
    string Notes,
    DateOnly? OriginalPublicationDate,
    IReadOnlyList<AuthorRef> Authors,
    IReadOnlyList<TagRef> Tags,
    IReadOnlyList<ReadDetails> Reads,
    IReadOnlyList<BookFileDetails> Files,
    long? CoverVersion,
    int HighlightCount,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    uint Version);

public class CreateBookRequest
{
    [Required]
    [StringLength(ApiLimits.TitleMaxLength)]
    public string Title { get; set; } = "";

    [EnumDataType(typeof(BookStatus))]
    public BookStatus Status { get; set; } = BookStatus.WantToRead;

    [Rating]
    public decimal? Rating { get; set; }

    /// <summary>Your notes about the book, in Markdown.</summary>
    [StringLength(ApiLimits.NotesMaxLength)]
    public string Notes { get; set; }

    public DateOnly? OriginalPublicationDate { get; set; }

    /// <summary>The book's authors, in the order they should be displayed.</summary>
    [MaxLength(ApiLimits.MaxAuthorsPerBook)]
    public List<Guid> AuthorIds { get; set; } = [];

    [MaxLength(ApiLimits.MaxTagsPerBook)]
    public List<Guid> TagIds { get; set; } = [];
}

public sealed class UpdateBookRequest : CreateBookRequest
{
    /// <summary>
    /// The <see cref="BookDetails.Version"/> this edit is based on. If the book changed since then
    /// (for example from another device), the update is rejected with 409 Conflict.
    /// </summary>
    [Required]
    public uint? Version { get; set; }
}

public enum BookSort
{
    Title,
    Author,
    Rating,
    PublicationDate,
    CreatedAt,
    UpdatedAt,

    /// <summary>Best match first; only meaningful together with a search term.</summary>
    Relevance,
}

/// <summary>Query string parameters for <c>GET /api/books</c>.</summary>
public sealed class BookListQuery
{
    /// <summary>
    /// Words to look for in the title, authors, tags and notes. Every word must match somewhere.
    /// Matching ignores case and accents and tolerates small typos in titles and author names.
    /// </summary>
    public string Search { get; set; }

    /// <summary>Only books with one of these statuses.</summary>
    public BookStatus[] Status { get; set; }

    /// <summary>Only books that have at least one of these tags.</summary>
    public Guid[] TagIds { get; set; }

    /// <summary>Only books by at least one of these authors.</summary>
    public Guid[] AuthorIds { get; set; }

    public decimal? RatingMin { get; set; }
    public decimal? RatingMax { get; set; }
    public DateOnly? PublishedFrom { get; set; }
    public DateOnly? PublishedTo { get; set; }

    /// <summary>Defaults to <see cref="BookSort.Relevance"/> when searching, otherwise <see cref="BookSort.Title"/>.</summary>
    public BookSort? Sort { get; set; }
    public SortDirection? Direction { get; set; }

    public int? Page { get; set; }
    public int? PageSize { get; set; }
}
