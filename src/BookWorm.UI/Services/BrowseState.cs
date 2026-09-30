using BookWorm.Contracts;

namespace BookWorm.UI.Services;

/// <summary>
/// Remembers the search, filters and page of the Books and Authors lists while you move around the
/// app, so coming back from a book shows the same list you left.
/// </summary>
public sealed class BrowseState
{
    public BookListQuery Books { get; set; } = new();

    public AuthorListQuery Authors { get; set; } = new();

    /// <summary>Author names by id, for showing the authors picked in the book filters.</summary>
    public Dictionary<Guid, string> AuthorNames { get; } = [];

    public void ShowBooksWithTag(Guid tagId) => Books = new BookListQuery { TagIds = [tagId] };

    public void ShowBooksByAuthor(AuthorRef author)
    {
        AuthorNames[author.Id] = author.Name;
        Books = new BookListQuery { AuthorIds = [author.Id] };
    }
}
