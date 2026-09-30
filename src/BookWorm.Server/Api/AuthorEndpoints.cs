using System.Linq.Expressions;
using BookWorm.Contracts;
using BookWorm.Server.Auth;
using BookWorm.Server.Data;
using BookWorm.Server.Notes;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace BookWorm.Server.Api;

internal static class AuthorEndpoints
{
    public static void MapAuthorEndpoints(this IEndpointRouteBuilder api)
    {
        var authors = api.MapGroup("/authors").WithTags("Authors");

        authors.MapGet("/", ListAuthors).WithSummary("Search, filter, sort and page through your authors.");
        authors.MapGet("/{id:guid}", GetAuthor).WithSummary("Get one author with their books.");
        authors.MapPost("/", CreateAuthor).WithValidation<CreateAuthorRequest>().WithSummary("Add an author to your library.");
        authors.MapPut("/{id:guid}", UpdateAuthor).WithValidation<UpdateAuthorRequest>().WithSummary("Replace an author's details.");
        authors.MapDelete("/{id:guid}", DeleteAuthor).WithSummary("Delete an author. Their books stay in your library.");
    }

    private static async Task<Ok<PagedResult<AuthorSummary>>> ListAuthors(
        [AsParameters] AuthorListQuery query, AppDbContext db, CancellationToken cancellationToken)
    {
        var (page, pageSize) = Paging.Normalize(query.Page, query.PageSize);
        var terms = SearchText.Terms(query.Search);

        var authors = db.Authors.AsNoTracking();

        foreach (var term in terms)
        {
            authors = authors.Where(Matches(term));
        }

        if (query.BornFrom is { } bornFrom)
        {
            authors = authors.Where(a => a.BirthDate >= bornFrom);
        }

        if (query.BornTo is { } bornTo)
        {
            authors = authors.Where(a => a.BirthDate <= bornTo);
        }

        if (query.DiedFrom is { } diedFrom)
        {
            authors = authors.Where(a => a.DeathDate >= diedFrom);
        }

        if (query.DiedTo is { } diedTo)
        {
            authors = authors.Where(a => a.DeathDate <= diedTo);
        }

        var totalCount = await authors.CountAsync(cancellationToken);

        var items = await Sort(authors, query, terms)
            .Page(page, pageSize)
            .Select(a => new AuthorSummary(a.Id, a.Name, a.BirthDate, a.DeathDate, a.Books.Count, a.CreatedAt, a.UpdatedAt))
            .ToListAsync(cancellationToken);

        return TypedResults.Ok(new PagedResult<AuthorSummary>(items, totalCount, page, pageSize));
    }

    private static async Task<Results<Ok<AuthorDetails>, NotFound>> GetAuthor(
        Guid id, AppDbContext db, CancellationToken cancellationToken)
    {
        var author = await FindDetailsAsync(db, id, cancellationToken);
        return author is null ? TypedResults.NotFound() : TypedResults.Ok(author);
    }

    private static async Task<Created<AuthorDetails>> CreateAuthor(
        CreateAuthorRequest request, AppDbContext db, ICurrentUser currentUser, CancellationToken cancellationToken)
    {
        var author = new Author
        {
            UserId = currentUser.RequireUserId(),
            Name = request.Name.Trim(),
            BirthDate = request.BirthDate,
            DeathDate = request.DeathDate,
        };

        db.Authors.Add(author);
        await db.SaveChangesAsync(cancellationToken);

        return TypedResults.Created($"/api/authors/{author.Id}", (await FindDetailsAsync(db, author.Id, cancellationToken)));
    }

    private static async Task<Results<Ok<AuthorDetails>, NotFound, Conflict<ProblemDetails>>> UpdateAuthor(
        Guid id, UpdateAuthorRequest request, AppDbContext db, CancellationToken cancellationToken)
    {
        var author = await db.Authors.SingleOrDefaultAsync(a => a.Id == id, cancellationToken);
        if (author is null)
        {
            return TypedResults.NotFound();
        }

        db.Entry(author).Property(a => a.Version).OriginalValue = request.Version.Value;

        author.Name = request.Name.Trim();
        author.BirthDate = request.BirthDate;
        author.DeathDate = request.DeathDate;
        db.Entry(author).Property(a => a.UpdatedAt).IsModified = true;

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return ApiErrors.ChangedElsewhere("author");
        }

        return TypedResults.Ok((await FindDetailsAsync(db, id, cancellationToken)));
    }

    private static async Task<Results<NoContent, NotFound>> DeleteAuthor(
        Guid id, AppDbContext db, ICurrentUser currentUser, INotesExportQueue notesExport, CancellationToken cancellationToken)
    {
        var deleted = await db.Authors.Where(a => a.Id == id).ExecuteDeleteAsync(cancellationToken);
        if (deleted == 0)
        {
            return TypedResults.NotFound();
        }

        // Exported notes mention authors; a bulk delete bypasses the automatic change tracking.
        notesExport.Enqueue(currentUser.RequireUserId());
        return TypedResults.NoContent();
    }

    private static Task<AuthorDetails> FindDetailsAsync(AppDbContext db, Guid id, CancellationToken cancellationToken) =>
        db.Authors.AsNoTracking()
            .Where(a => a.Id == id)
            .Select(a => new AuthorDetails(
                a.Id,
                a.Name,
                a.BirthDate,
                a.DeathDate,
                a.Books
                    .OrderBy(ba => ba.Book.Title)
                    .Select(ba => new BookRef(ba.Book.Id, ba.Book.Title, ba.Book.Status, ba.Book.Rating))
                    .ToList(),
                a.CreatedAt,
                a.UpdatedAt,
                a.Version))
            .SingleOrDefaultAsync(cancellationToken);

    private static Expression<Func<Author, bool>> Matches(string term)
    {
        var pattern = SearchText.ContainsPattern(term);

        Expression<Func<Author, bool>> contains = a =>
            EF.Functions.ILike(EF.Functions.Unaccent(a.Name), EF.Functions.Unaccent(pattern), SearchText.LikeEscape);

        if (!SearchText.AllowsFuzzyMatch(term))
        {
            return contains;
        }

        return contains.OrElse(a =>
            EF.Functions.TrigramsStrictWordSimilarity(EF.Functions.Unaccent(term), EF.Functions.Unaccent(a.Name)) >= SearchText.FuzzyThreshold);
    }

    private static IQueryable<Author> Sort(IQueryable<Author> authors, AuthorListQuery query, string[] terms)
    {
        var sort = query.Sort ?? (terms.Length > 0 ? AuthorSort.Relevance : AuthorSort.Name);
        if (sort == AuthorSort.Relevance && terms.Length == 0)
        {
            sort = AuthorSort.Name;
        }

        var descending = query.Direction switch
        {
            SortDirection.Asc => false,
            SortDirection.Desc => true,
            _ => sort is AuthorSort.BookCount or AuthorSort.CreatedAt or AuthorSort.Relevance,
        };

        IOrderedQueryable<Author> ordered;
        switch (sort)
        {
            case AuthorSort.Relevance:
                var text = string.Join(' ', terms);
                ordered = authors
                    .OrderByDescending(a => EF.Functions.TrigramsStrictWordSimilarity(EF.Functions.Unaccent(text), EF.Functions.Unaccent(a.Name)))
                    .ThenByDescending(a => EF.Functions.TrigramsSimilarity(EF.Functions.Unaccent(text), EF.Functions.Unaccent(a.Name)))
                    .ThenBy(a => a.Name);
                break;
            case AuthorSort.BirthDate:
                ordered = authors.OrderBy(a => a.BirthDate == null);
                ordered = descending ? ordered.ThenByDescending(a => a.BirthDate) : ordered.ThenBy(a => a.BirthDate);
                ordered = ordered.ThenBy(a => a.Name);
                break;
            case AuthorSort.DeathDate:
                ordered = authors.OrderBy(a => a.DeathDate == null);
                ordered = descending ? ordered.ThenByDescending(a => a.DeathDate) : ordered.ThenBy(a => a.DeathDate);
                ordered = ordered.ThenBy(a => a.Name);
                break;
            case AuthorSort.BookCount:
                ordered = descending ? authors.OrderByDescending(a => a.Books.Count) : authors.OrderBy(a => a.Books.Count);
                ordered = ordered.ThenBy(a => a.Name);
                break;
            case AuthorSort.CreatedAt:
                ordered = descending ? authors.OrderByDescending(a => a.CreatedAt) : authors.OrderBy(a => a.CreatedAt);
                break;
            default:
                ordered = descending ? authors.OrderByDescending(a => a.Name) : authors.OrderBy(a => a.Name);
                break;
        }

        return ordered.ThenBy(a => a.Id);
    }
}
