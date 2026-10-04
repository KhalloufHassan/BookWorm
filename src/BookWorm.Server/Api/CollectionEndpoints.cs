using BookWorm.Contracts;
using BookWorm.Server.Auth;
using BookWorm.Server.Data;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace BookWorm.Server.Api;

internal static class CollectionEndpoints
{
    public static void MapCollectionEndpoints(this IEndpointRouteBuilder api)
    {
        var collections = api.MapGroup("/collections").WithTags("Collections");

        collections.MapGet("/", ListCollections).WithSummary("List your collections, optionally filtered by name.");
        collections.MapGet("/{id:guid}", GetCollection).WithSummary("Get one collection with its books in order.");
        collections.MapPost("/", CreateCollection).WithValidation<CreateCollectionRequest>().WithSummary("Create an empty collection.");
        collections.MapPut("/{id:guid}", UpdateCollection).WithValidation<UpdateCollectionRequest>().WithSummary("Rename a collection or change its type.");
        collections.MapPut("/{id:guid}/books", SetBooks).WithValidation<SetCollectionBooksRequest>()
            .WithSummary("Replace a collection's books and their order.");
        collections.MapDelete("/{id:guid}", DeleteCollection).WithSummary("Delete a collection. Its books stay in your library.");
    }

    private static async Task<Ok<List<CollectionSummary>>> ListCollections(
        string search, AppDbContext db, CancellationToken cancellationToken)
    {
        var collections = db.Collections.AsNoTracking();

        foreach (var term in SearchText.Terms(search))
        {
            var pattern = SearchText.ContainsPattern(term);
            collections = collections.Where(c => EF.Functions.ILike(EF.Functions.Unaccent(c.Name), EF.Functions.Unaccent(pattern), SearchText.LikeEscape));
        }

        var items = await collections
            .OrderBy(c => c.Name)
            .ThenBy(c => c.Id)
            .Select(c => new CollectionSummary(c.Id, c.Name, c.Type, c.Books.Count, c.CreatedAt, c.UpdatedAt, c.Version))
            .ToListAsync(cancellationToken);

        return TypedResults.Ok(items);
    }

    private static async Task<Results<Ok<CollectionDetails>, NotFound>> GetCollection(
        Guid id, AppDbContext db, CancellationToken cancellationToken)
    {
        var collection = await FindDetailsAsync(db, id, cancellationToken);
        return collection is null ? TypedResults.NotFound() : TypedResults.Ok(collection);
    }

    private static async Task<Results<Created<CollectionDetails>, Conflict<ProblemDetails>>> CreateCollection(
        CreateCollectionRequest request, AppDbContext db, ICurrentUser currentUser, CancellationToken cancellationToken)
    {
        var name = request.Name.Trim();
        if (await NameTakenAsync(db, name, exceptId: null, cancellationToken))
        {
            return NameConflict(name);
        }

        var collection = new Collection { UserId = currentUser.RequireUserId(), Name = name, Type = request.Type };
        db.Collections.Add(collection);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ApiErrors.IsUniqueViolation(ex))
        {
            return NameConflict(name);
        }

        return TypedResults.Created($"/api/collections/{collection.Id}", await FindDetailsAsync(db, collection.Id, cancellationToken));
    }

    private static async Task<Results<Ok<CollectionDetails>, NotFound, Conflict<ProblemDetails>>> UpdateCollection(
        Guid id, UpdateCollectionRequest request, AppDbContext db, CancellationToken cancellationToken)
    {
        var collection = await db.Collections.SingleOrDefaultAsync(c => c.Id == id, cancellationToken);
        if (collection is null)
        {
            return TypedResults.NotFound();
        }

        var name = request.Name.Trim();
        if (await NameTakenAsync(db, name, exceptId: id, cancellationToken))
        {
            return NameConflict(name);
        }

        db.Entry(collection).Property(c => c.Version).OriginalValue = request.Version.Value;
        collection.Name = name;
        collection.Type = request.Type;
        db.Entry(collection).Property(c => c.UpdatedAt).IsModified = true;

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return ApiErrors.ChangedElsewhere("collection");
        }
        catch (DbUpdateException ex) when (ApiErrors.IsUniqueViolation(ex))
        {
            return NameConflict(name);
        }

        return TypedResults.Ok(await FindDetailsAsync(db, id, cancellationToken));
    }

    private static async Task<Results<Ok<CollectionDetails>, NotFound, ValidationProblem>> SetBooks(
        Guid id, SetCollectionBooksRequest request, AppDbContext db, CancellationToken cancellationToken)
    {
        var collection = await db.Collections.Include(c => c.Books).SingleOrDefaultAsync(c => c.Id == id, cancellationToken);
        if (collection is null)
        {
            return TypedResults.NotFound();
        }

        var bookIds = request.BookIds.Distinct().ToList();
        if (bookIds.Count != request.BookIds.Count)
        {
            return ApiErrors.Validation(nameof(SetCollectionBooksRequest.BookIds), "A book can only be in a collection once.");
        }

        var found = await db.Books.CountAsync(b => bookIds.Contains(b.Id), cancellationToken);
        if (found != bookIds.Count)
        {
            return ApiErrors.Validation(nameof(SetCollectionBooksRequest.BookIds), "One or more books don't exist in your library.");
        }

        var existing = collection.Books.ToDictionary(cb => cb.BookId);
        collection.Books.RemoveAll(cb => !bookIds.Contains(cb.BookId));
        for (var position = 0; position < bookIds.Count; position++)
        {
            if (existing.TryGetValue(bookIds[position], out var member))
            {
                member.Position = position;
            }
            else
            {
                collection.Books.Add(new CollectionBook { BookId = bookIds[position], Position = position });
            }
        }

        // Changing the books counts as a change to the collection, so its version moves on too.
        db.Entry(collection).Property(c => c.UpdatedAt).IsModified = true;
        await db.SaveChangesAsync(cancellationToken);

        return TypedResults.Ok(await FindDetailsAsync(db, id, cancellationToken));
    }

    private static async Task<Results<NoContent, NotFound>> DeleteCollection(
        Guid id, AppDbContext db, CancellationToken cancellationToken)
    {
        var deleted = await db.Collections.Where(c => c.Id == id).ExecuteDeleteAsync(cancellationToken);
        return deleted == 0 ? TypedResults.NotFound() : TypedResults.NoContent();
    }

    private static async Task<CollectionDetails> FindDetailsAsync(AppDbContext db, Guid id, CancellationToken cancellationToken)
    {
        var collection = await db.Collections.AsNoTracking()
            .Where(c => c.Id == id)
            .Select(c => new { c.Id, c.Name, c.Type, c.CreatedAt, c.UpdatedAt, c.Version })
            .SingleOrDefaultAsync(cancellationToken);
        if (collection is null)
        {
            return null;
        }

        var books = await db.CollectionBooks.AsNoTracking()
            .Where(cb => cb.CollectionId == id)
            .OrderBy(cb => cb.Position)
            .Select(cb => cb.Book)
            .Select(BookEndpoints.Summary)
            .AsSplitQuery()
            .ToListAsync(cancellationToken);

        return new CollectionDetails(collection.Id, collection.Name, collection.Type, books, collection.CreatedAt, collection.UpdatedAt, collection.Version);
    }

    /// <summary>Collection names are unique per user, ignoring case (enforced by a unique index too).</summary>
    private static Task<bool> NameTakenAsync(AppDbContext db, string name, Guid? exceptId, CancellationToken cancellationToken)
    {
        var pattern = SearchText.ExactPattern(name);
        return db.Collections.AnyAsync(
            c => c.Id != exceptId && EF.Functions.ILike(c.Name, pattern, SearchText.LikeEscape),
            cancellationToken);
    }

    private static Conflict<ProblemDetails> NameConflict(string name) =>
        ApiErrors.Conflict($"You already have a collection named \"{name}\".");
}
