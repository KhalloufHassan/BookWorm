using BookWorm.Contracts;

namespace BookWorm.Server.Data;

/// <summary>
/// One file of a book, at most one per format. The content lives on disk (see
/// <c>LibraryStorage</c>); replacing the file keeps this row and updates its hash.
/// </summary>
public sealed class BookFile : IAuditable
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid BookId { get; set; }

    public BookFormat Format { get; set; }

    /// <summary>The name the file was uploaded with, used when downloading it.</summary>
    public required string FileName { get; set; }

    public long SizeBytes { get; set; }

    /// <summary>SHA-256 of the content, lowercase hex.</summary>
    public required string Sha256 { get; set; }

    public DateTimeOffset UploadedAt { get; set; }

    /// <summary>Where the owner last was while browsing the file with no read in progress.</summary>
    public string BrowseLocation { get; set; }
    public double? BrowseProgress { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public Book Book { get; set; }
}
