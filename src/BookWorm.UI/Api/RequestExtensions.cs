using BookWorm.Contracts;

namespace BookWorm.UI.Api;

public static class RequestExtensions
{
    /// <summary>An update that keeps everything about the book as it is; change what you need.</summary>
    public static UpdateBookRequest ToUpdateRequest(this BookDetails book) => new()
    {
        Title = book.Title,
        Status = book.Status,
        Rating = book.Rating,
        Notes = book.Notes,
        OriginalPublicationDate = book.OriginalPublicationDate,
        AuthorIds = book.Authors.Select(a => a.Id).ToList(),
        TagIds = book.Tags.Select(t => t.Id).ToList(),
        Version = book.Version,
    };
}
