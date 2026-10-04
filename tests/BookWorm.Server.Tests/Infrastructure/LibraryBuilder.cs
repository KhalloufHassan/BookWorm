using BookWorm.Contracts;
using BookWorm.UI.Api;

namespace BookWorm.Server.Tests.Infrastructure;

/// <summary>Shorthands for filling a test user's library.</summary>
internal static class LibraryBuilder
{
    public static Task<AuthorDetails> AddAuthorAsync(this BookWormApiClient api, string name, DateOnly? born = null, DateOnly? died = null) =>
        api.CreateAuthorAsync(new CreateAuthorRequest { Name = name, BirthDate = born, DeathDate = died });

    public static Task<TagSummary> AddTagAsync(this BookWormApiClient api, string name) =>
        api.CreateTagAsync(new CreateTagRequest { Name = name });

    public static Task<CollectionDetails> AddCollectionAsync(this BookWormApiClient api, string name, CollectionType type = CollectionType.Series) =>
        api.CreateCollectionAsync(new CreateCollectionRequest { Name = name, Type = type });

    public static Task<BookDetails> AddBookAsync(
        this BookWormApiClient api,
        string title,
        IEnumerable<Guid> authorIds = null,
        IEnumerable<Guid> tagIds = null,
        BookStatus status = BookStatus.WantToRead,
        decimal? rating = null,
        DateOnly? published = null,
        string notes = null) =>
        api.CreateBookAsync(new CreateBookRequest
        {
            Title = title,
            AuthorIds = authorIds?.ToList() ?? [],
            TagIds = tagIds?.ToList() ?? [],
            Status = status,
            Rating = rating,
            OriginalPublicationDate = published,
            Notes = notes,
        });

    public static UpdateBookRequest ToUpdate(this BookDetails book) => new()
    {
        Title = book.Title,
        Status = book.Status,
        Rating = book.Rating,
        Notes = book.Notes,
        OriginalPublicationDate = book.OriginalPublicationDate,
        AuthorIds = book.Authors.Select(a => a.Id).ToList(),
        TagIds = book.Tags.Select(t => t.Id).ToList(),
        CollectionIds = book.Collections.Select(c => c.Id).ToList(),
        Version = book.Version,
    };
}
