using BookWorm.Contracts;
using BookWorm.Server.Auth;
using BookWorm.Server.Data;
using BookWorm.Server.Notes;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace BookWorm.Server.Api;

internal static class TagEndpoints
{
    public static void MapTagEndpoints(this IEndpointRouteBuilder api)
    {
        var tags = api.MapGroup("/tags").WithTags("Tags");

        tags.MapGet("/", ListTags).WithSummary("List your tags, optionally filtered by name.");
        tags.MapPost("/", CreateTag).WithValidation<CreateTagRequest>().WithSummary("Create a tag.");
        tags.MapPut("/{id:guid}", UpdateTag).WithValidation<UpdateTagRequest>().WithSummary("Rename a tag.");
        tags.MapDelete("/{id:guid}", DeleteTag).WithSummary("Delete a tag and remove it from all books.");
    }

    private static async Task<Ok<List<TagSummary>>> ListTags(
        string search, AppDbContext db, CancellationToken cancellationToken)
    {
        var tags = db.Tags.AsNoTracking();

        foreach (var term in SearchText.Terms(search))
        {
            var pattern = SearchText.ContainsPattern(term);
            tags = tags.Where(t => EF.Functions.ILike(EF.Functions.Unaccent(t.Name), EF.Functions.Unaccent(pattern), SearchText.LikeEscape));
        }

        var items = await tags
            .OrderBy(t => t.Name)
            .ThenBy(t => t.Id)
            .Select(t => new TagSummary(t.Id, t.Name, t.Books.Count, t.CreatedAt, t.UpdatedAt, t.Version))
            .ToListAsync(cancellationToken);

        return TypedResults.Ok(items);
    }

    private static async Task<Results<Created<TagSummary>, Conflict<ProblemDetails>>> CreateTag(
        CreateTagRequest request, AppDbContext db, ICurrentUser currentUser, CancellationToken cancellationToken)
    {
        var name = request.Name.Trim();
        if (await NameTakenAsync(db, name, exceptId: null, cancellationToken))
        {
            return NameConflict(name);
        }

        var tag = new Tag { UserId = currentUser.RequireUserId(), Name = name };
        db.Tags.Add(tag);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ApiErrors.IsUniqueViolation(ex))
        {
            return NameConflict(name);
        }

        return TypedResults.Created($"/api/tags/{tag.Id}", ToSummary(tag, bookCount: 0));
    }

    private static async Task<Results<Ok<TagSummary>, NotFound, Conflict<ProblemDetails>>> UpdateTag(
        Guid id, UpdateTagRequest request, AppDbContext db, CancellationToken cancellationToken)
    {
        var tag = await db.Tags.SingleOrDefaultAsync(t => t.Id == id, cancellationToken);
        if (tag is null)
        {
            return TypedResults.NotFound();
        }

        var name = request.Name.Trim();
        if (await NameTakenAsync(db, name, exceptId: id, cancellationToken))
        {
            return NameConflict(name);
        }

        db.Entry(tag).Property(t => t.Version).OriginalValue = request.Version.Value;
        tag.Name = name;
        db.Entry(tag).Property(t => t.UpdatedAt).IsModified = true;

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return ApiErrors.ChangedElsewhere("tag");
        }
        catch (DbUpdateException ex) when (ApiErrors.IsUniqueViolation(ex))
        {
            return NameConflict(name);
        }

        var bookCount = await db.Tags.Where(t => t.Id == id).Select(t => t.Books.Count).SingleAsync(cancellationToken);
        return TypedResults.Ok(ToSummary(tag, bookCount));
    }

    private static async Task<Results<NoContent, NotFound>> DeleteTag(
        Guid id, AppDbContext db, ICurrentUser currentUser, INotesExportQueue notesExport, CancellationToken cancellationToken)
    {
        var deleted = await db.Tags.Where(t => t.Id == id).ExecuteDeleteAsync(cancellationToken);
        if (deleted == 0)
        {
            return TypedResults.NotFound();
        }

        // Exported notes mention tags; a bulk delete bypasses the automatic change tracking.
        notesExport.Enqueue(currentUser.RequireUserId());
        return TypedResults.NoContent();
    }

    /// <summary>Tag names are unique per user, ignoring case (enforced by a unique index too).</summary>
    private static Task<bool> NameTakenAsync(AppDbContext db, string name, Guid? exceptId, CancellationToken cancellationToken)
    {
        var pattern = SearchText.ExactPattern(name);
        return db.Tags.AnyAsync(
            t => t.Id != exceptId && EF.Functions.ILike(t.Name, pattern, SearchText.LikeEscape),
            cancellationToken);
    }

    private static Conflict<ProblemDetails> NameConflict(string name) =>
        ApiErrors.Conflict($"You already have a tag named \"{name}\".");

    private static TagSummary ToSummary(Tag tag, int bookCount) =>
        new(tag.Id, tag.Name, bookCount, tag.CreatedAt, tag.UpdatedAt, tag.Version);
}
