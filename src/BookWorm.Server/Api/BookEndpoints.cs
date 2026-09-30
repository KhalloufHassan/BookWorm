using System.Linq.Expressions;
using BookWorm.Contracts;
using BookWorm.Server.Auth;
using BookWorm.Server.Data;
using BookWorm.Server.Notes;
using BookWorm.Server.Storage;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace BookWorm.Server.Api;

internal static class BookEndpoints
{
    public static void MapBookEndpoints(this IEndpointRouteBuilder api)
    {
        var books = api.MapGroup("/books").WithTags("Books");

        books.MapGet("/", ListBooks).WithSummary("Search, filter, sort and page through your books.");
        books.MapGet("/{id:guid}", GetBook).WithSummary("Get one book with its notes and reads.");
        books.MapPost("/", CreateBook).WithValidation<CreateBookRequest>().WithSummary("Add a book to your library.");
        books.MapPut("/{id:guid}", UpdateBook).WithValidation<UpdateBookRequest>().WithSummary("Replace a book's details.");
        books.MapDelete("/{id:guid}", DeleteBook).WithSummary("Delete a book with its reads, files, cover and highlights.");
    }

    private static async Task<Ok<PagedResult<BookSummary>>> ListBooks(
        [AsParameters] BookListQuery query, AppDbContext db, CancellationToken cancellationToken)
    {
        var (page, pageSize) = Paging.Normalize(query.Page, query.PageSize);
        var terms = SearchText.Terms(query.Search);

        var books = db.Books.AsNoTracking();

        foreach (var term in terms)
        {
            books = books.Where(Matches(term));
        }

        if (query.Status is { Length: > 0 } statuses)
        {
            books = books.Where(b => statuses.Contains(b.Status));
        }

        if (query.TagIds is { Length: > 0 } tagIds)
        {
            books = books.Where(b => b.Tags.Any(t => tagIds.Contains(t.Id)));
        }

        if (query.AuthorIds is { Length: > 0 } authorIds)
        {
            books = books.Where(b => b.Authors.Any(ba => authorIds.Contains(ba.AuthorId)));
        }

        if (query.RatingMin is { } ratingMin)
        {
            books = books.Where(b => b.Rating >= ratingMin);
        }

        if (query.RatingMax is { } ratingMax)
        {
            books = books.Where(b => b.Rating <= ratingMax);
        }

        if (query.PublishedFrom is { } publishedFrom)
        {
            books = books.Where(b => b.OriginalPublicationDate >= publishedFrom);
        }

        if (query.PublishedTo is { } publishedTo)
        {
            books = books.Where(b => b.OriginalPublicationDate <= publishedTo);
        }

        var totalCount = await books.CountAsync(cancellationToken);

        var items = await Sort(books, query, terms)
            .Page(page, pageSize)
            .Select(Summary)
            .AsSplitQuery()
            .ToListAsync(cancellationToken);

        return TypedResults.Ok(new PagedResult<BookSummary>(items, totalCount, page, pageSize));
    }

    private static async Task<Results<Ok<BookDetails>, NotFound>> GetBook(
        Guid id, AppDbContext db, CancellationToken cancellationToken)
    {
        var book = await FindDetailsAsync(db, id, cancellationToken);
        return book is null ? TypedResults.NotFound() : TypedResults.Ok(book);
    }

    private static async Task<Results<Created<BookDetails>, ValidationProblem>> CreateBook(
        CreateBookRequest request, AppDbContext db, ICurrentUser currentUser, CancellationToken cancellationToken)
    {
        var authorIds = request.AuthorIds.Distinct().ToList();
        var tagIds = request.TagIds.Distinct().ToList();

        var tags = await db.Tags.Where(t => tagIds.Contains(t.Id)).ToListAsync(cancellationToken);
        if (await ValidateReferencesAsync(db, authorIds, tagIds, tags, cancellationToken) is { } problem)
        {
            return problem;
        }

        var book = new Book
        {
            UserId = currentUser.RequireUserId(),
            Title = request.Title.Trim(),
        };
        Apply(request, book);

        for (var position = 0; position < authorIds.Count; position++)
        {
            book.Authors.Add(new BookAuthor { AuthorId = authorIds[position], Position = position });
        }

        book.Tags.AddRange(tags);

        db.Books.Add(book);
        await db.SaveChangesAsync(cancellationToken);

        var details = await FindDetailsAsync(db, book.Id, cancellationToken);
        return TypedResults.Created($"/api/books/{book.Id}", details);
    }

    private static async Task<Results<Ok<BookDetails>, NotFound, ValidationProblem, Conflict<ProblemDetails>>> UpdateBook(
        Guid id, UpdateBookRequest request, AppDbContext db, TimeProvider timeProvider, CancellationToken cancellationToken)
    {
        var book = await db.Books
            .Include(b => b.Authors)
            .Include(b => b.Tags)
            .SingleOrDefaultAsync(b => b.Id == id, cancellationToken);
        if (book is null)
        {
            return TypedResults.NotFound();
        }

        var authorIds = request.AuthorIds.Distinct().ToList();
        var tagIds = request.TagIds.Distinct().ToList();

        var tags = await db.Tags.Where(t => tagIds.Contains(t.Id)).ToListAsync(cancellationToken);
        if (await ValidateReferencesAsync(db, authorIds, tagIds, tags, cancellationToken) is { } problem)
        {
            return problem;
        }

        // Only save if nobody else changed the book since the client loaded it.
        db.Entry(book).Property(b => b.Version).OriginalValue = request.Version.Value;

        var becameFinished = book.Status != BookStatus.Finished && request.Status == BookStatus.Finished;

        book.Title = request.Title.Trim();
        Apply(request, book);

        // Marking the book finished also finishes the read that was still going.
        if (becameFinished)
        {
            var now = timeProvider.GetUtcNow();
            var ongoing = await db.Reads.Where(r => r.BookId == id && r.Status == ReadStatus.CurrentlyReading).ToListAsync(cancellationToken);
            foreach (var read in ongoing)
            {
                read.Status = ReadStatus.Finished;
                read.FinishedAt = read.StartedAt is { } started && now < started ? started : now;
            }
        }

        book.Authors.RemoveAll(ba => !authorIds.Contains(ba.AuthorId));
        for (var position = 0; position < authorIds.Count; position++)
        {
            var existing = book.Authors.Find(ba => ba.AuthorId == authorIds[position]);
            if (existing is null)
            {
                book.Authors.Add(new BookAuthor { AuthorId = authorIds[position], Position = position });
            }
            else
            {
                existing.Position = position;
            }
        }

        book.Tags.RemoveAll(t => !tagIds.Contains(t.Id));
        var addedTags = tags.Where(tag => !book.Tags.Contains(tag)).ToList();
        book.Tags.AddRange(addedTags);

        // Always touch the row, even when only authors or tags changed, so the version check applies.
        db.Entry(book).Property(b => b.UpdatedAt).IsModified = true;

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return ApiErrors.ChangedElsewhere("book");
        }

        return TypedResults.Ok((await FindDetailsAsync(db, id, cancellationToken)));
    }

    private static async Task<Results<NoContent, NotFound>> DeleteBook(
        Guid id,
        AppDbContext db,
        ICurrentUser currentUser,
        LibraryStorage storage,
        INotesExportQueue notesExport,
        CancellationToken cancellationToken)
    {
        var deleted = await db.Books.Where(b => b.Id == id).ExecuteDeleteAsync(cancellationToken);
        if (deleted == 0)
        {
            return TypedResults.NotFound();
        }

        var userId = currentUser.RequireUserId();
        storage.DeleteBook(userId, id);
        notesExport.Enqueue(userId);
        return TypedResults.NoContent();
    }

    private static void Apply(CreateBookRequest request, Book book)
    {
        book.Status = request.Status;
        book.Rating = request.Rating;
        book.Notes = string.IsNullOrWhiteSpace(request.Notes) ? null : request.Notes;
        book.OriginalPublicationDate = request.OriginalPublicationDate;
    }

    /// <summary>Authors and tags must exist in the caller's own library (the query filters guarantee that).</summary>
    private static async Task<ValidationProblem> ValidateReferencesAsync(
        AppDbContext db, List<Guid> authorIds, List<Guid> tagIds, List<Tag> tags, CancellationToken cancellationToken)
    {
        var foundAuthors = await db.Authors.CountAsync(a => authorIds.Contains(a.Id), cancellationToken);
        if (foundAuthors != authorIds.Count)
        {
            return ApiErrors.Validation(nameof(CreateBookRequest.AuthorIds), "One or more authors don't exist in your library.");
        }

        if (tags.Count != tagIds.Count)
        {
            return ApiErrors.Validation(nameof(CreateBookRequest.TagIds), "One or more tags don't exist in your library.");
        }

        return null;
    }

    private static Task<BookDetails> FindDetailsAsync(AppDbContext db, Guid id, CancellationToken cancellationToken) =>
        db.Books.AsNoTracking()
            .Where(b => b.Id == id)
            .Select(Details)
            .AsSplitQuery()
            .SingleOrDefaultAsync(cancellationToken);

    /// <summary>A search word matches a book if it appears in the title, notes, an author or a tag.</summary>
    private static Expression<Func<Book, bool>> Matches(string term)
    {
        var pattern = SearchText.ContainsPattern(term);

        Expression<Func<Book, bool>> contains = b =>
            EF.Functions.ILike(EF.Functions.Unaccent(b.Title), EF.Functions.Unaccent(pattern), SearchText.LikeEscape)
            || (b.Notes != null && EF.Functions.ILike(EF.Functions.Unaccent(b.Notes), EF.Functions.Unaccent(pattern), SearchText.LikeEscape))
            || b.Authors.Any(ba => EF.Functions.ILike(EF.Functions.Unaccent(ba.Author.Name), EF.Functions.Unaccent(pattern), SearchText.LikeEscape))
            || b.Tags.Any(t => EF.Functions.ILike(EF.Functions.Unaccent(t.Name), EF.Functions.Unaccent(pattern), SearchText.LikeEscape));

        if (!SearchText.AllowsFuzzyMatch(term))
        {
            return contains;
        }

        // Typo tolerance for titles and author names.
        return contains.OrElse(b =>
            EF.Functions.TrigramsStrictWordSimilarity(EF.Functions.Unaccent(term), EF.Functions.Unaccent(b.Title)) >= SearchText.FuzzyThreshold
            || b.Authors.Any(ba =>
                EF.Functions.TrigramsStrictWordSimilarity(EF.Functions.Unaccent(term), EF.Functions.Unaccent(ba.Author.Name)) >= SearchText.FuzzyThreshold));
    }

    private static IQueryable<Book> Sort(IQueryable<Book> books, BookListQuery query, string[] terms)
    {
        var sort = query.Sort ?? (terms.Length > 0 ? BookSort.Relevance : BookSort.Title);
        if (sort == BookSort.Relevance && terms.Length == 0)
        {
            sort = BookSort.Title;
        }

        // Each sort has a natural default direction; newest and best-rated come first.
        var descending = query.Direction switch
        {
            SortDirection.Asc => false,
            SortDirection.Desc => true,
            _ => sort is BookSort.Rating or BookSort.CreatedAt or BookSort.UpdatedAt or BookSort.Relevance,
        };

        IOrderedQueryable<Book> ordered;
        switch (sort)
        {
            case BookSort.Relevance:
                var text = string.Join(' ', terms);
                // Best word match first; among equals, the title closest to the search as a whole.
                ordered = books
                    .OrderByDescending(b => EF.Functions.TrigramsStrictWordSimilarity(EF.Functions.Unaccent(text), EF.Functions.Unaccent(b.Title)))
                    .ThenByDescending(b => EF.Functions.TrigramsSimilarity(EF.Functions.Unaccent(text), EF.Functions.Unaccent(b.Title)))
                    .ThenBy(b => b.Title);
                break;
            case BookSort.Author:
                Expression<Func<Book, string>> firstAuthor = b =>
                    b.Authors.OrderBy(ba => ba.Position).Select(ba => ba.Author.Name).FirstOrDefault();
                ordered = books.OrderBy(b => b.Authors.Count == 0);
                ordered = descending ? ordered.ThenByDescending(firstAuthor) : ordered.ThenBy(firstAuthor);
                ordered = ordered.ThenBy(b => b.Title);
                break;
            case BookSort.Rating:
                ordered = books.OrderBy(b => b.Rating == null);
                ordered = descending ? ordered.ThenByDescending(b => b.Rating) : ordered.ThenBy(b => b.Rating);
                ordered = ordered.ThenBy(b => b.Title);
                break;
            case BookSort.PublicationDate:
                ordered = books.OrderBy(b => b.OriginalPublicationDate == null);
                ordered = descending
                    ? ordered.ThenByDescending(b => b.OriginalPublicationDate)
                    : ordered.ThenBy(b => b.OriginalPublicationDate);
                ordered = ordered.ThenBy(b => b.Title);
                break;
            case BookSort.CreatedAt:
                ordered = descending ? books.OrderByDescending(b => b.CreatedAt) : books.OrderBy(b => b.CreatedAt);
                break;
            case BookSort.UpdatedAt:
                ordered = descending ? books.OrderByDescending(b => b.UpdatedAt) : books.OrderBy(b => b.UpdatedAt);
                break;
            default:
                ordered = descending ? books.OrderByDescending(b => b.Title) : books.OrderBy(b => b.Title);
                break;
        }

        // A unique tie-breaker keeps paging stable.
        return ordered.ThenBy(b => b.Id);
    }

    private static readonly Expression<Func<Book, BookSummary>> Summary = b => new BookSummary(
        b.Id,
        b.Title,
        b.Status,
        b.Rating,
        b.OriginalPublicationDate,
        b.Authors.OrderBy(ba => ba.Position).Select(ba => new AuthorRef(ba.Author.Id, ba.Author.Name)).ToList(),
        b.Tags.OrderBy(t => t.Name).Select(t => new TagRef(t.Id, t.Name)).ToList(),
        Covers.Version(b.CoverUpdatedAt),
        b.Files.OrderBy(f => f.Format).Select(f => f.Format).ToList(),
        b.Reads
            .Where(r => r.Status == ReadStatus.CurrentlyReading && r.Progress != null)
            .OrderByDescending(r => r.LastOpenedAt)
            .Select(r => r.Progress)
            .FirstOrDefault(),
        b.CreatedAt,
        b.UpdatedAt);

    private static readonly Expression<Func<Book, BookDetails>> Details = b => new BookDetails(
        b.Id,
        b.Title,
        b.Status,
        b.Rating,
        b.Notes,
        b.OriginalPublicationDate,
        b.Authors.OrderBy(ba => ba.Position).Select(ba => new AuthorRef(ba.Author.Id, ba.Author.Name)).ToList(),
        b.Tags.OrderBy(t => t.Name).Select(t => new TagRef(t.Id, t.Name)).ToList(),
        b.Reads
            .OrderBy(r => r.StartedAt == null)
            .ThenByDescending(r => r.StartedAt)
            .ThenByDescending(r => r.CreatedAt)
            .Select(r => new ReadDetails(r.Id, r.BookId, r.Status, r.StartedAt, r.FinishedAt, r.FileId, r.Location, r.Progress, r.LastOpenedAt, r.CreatedAt, r.UpdatedAt, r.Version))
            .ToList(),
        b.Files
            .OrderBy(f => f.Format)
            .Select(f => new BookFileDetails(f.Id, f.Format, f.FileName, f.SizeBytes, f.Sha256, f.UploadedAt, f.BrowseLocation, f.BrowseProgress))
            .ToList(),
        Covers.Version(b.CoverUpdatedAt),
        b.Highlights.Count,
        b.CreatedAt,
        b.UpdatedAt,
        b.Version);
}
