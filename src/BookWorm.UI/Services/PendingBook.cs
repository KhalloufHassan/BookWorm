using BookWorm.Contracts;

namespace BookWorm.UI.Services;

/// <summary>A book file picked with "Add book from file", carried to the book form that it pre-fills.</summary>
public sealed record PendingBookFile(PickedFile File, BookFormat Format, FileMetadata Metadata, string CoverPreviewUrl);

/// <summary>Holds the picked file between the Books page and the form.</summary>
public sealed class PendingBook
{
    public PendingBookFile Current { get; set; }
}
