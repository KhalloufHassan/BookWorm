namespace BookWorm.Server.Data;

/// <summary>Links a book to one of its authors.</summary>
public sealed class BookAuthor
{
    public Guid BookId { get; set; }
    public Guid AuthorId { get; set; }

    /// <summary>Where the author appears in the book's author list, starting at 0.</summary>
    public int Position { get; set; }

    public Book Book { get; set; }
    public Author Author { get; set; }
}
