using System.Linq.Expressions;
using BookWorm.Contracts;
using BookWorm.Server.Data;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace BookWorm.Server.Api;

internal static class HighlightEndpoints
{
    public static void MapHighlightEndpoints(this IEndpointRouteBuilder api)
    {
        var highlights = api.MapGroup("/books/{bookId:guid}/highlights").WithTags("Highlights");

        highlights.MapGet("/", ListHighlights).WithSummary("List a book's highlights in reading order.");
        highlights.MapPost("/", CreateHighlight).WithValidation<CreateHighlightRequest>().WithSummary("Highlight a passage of a book file.");
        highlights.MapPut("/{id:guid}", UpdateHighlight).WithValidation<UpdateHighlightRequest>().WithSummary("Change a highlight's color or note.");
        highlights.MapPut("/{id:guid}/anchor", ReanchorHighlight).WithValidation<ReanchorHighlightRequest>()
            .WithSummary("Record where a highlight is in the current version of its file, or that it can't be found there.");
        highlights.MapDelete("/{id:guid}", DeleteHighlight).WithSummary("Delete a highlight and its note.");
    }

    private static async Task<Results<Ok<List<HighlightDetails>>, NotFound>> ListHighlights(
        Guid bookId, AppDbContext db, CancellationToken cancellationToken)
    {
        if (!await db.Books.AnyAsync(b => b.Id == bookId, cancellationToken))
        {
            return TypedResults.NotFound();
        }

        var highlights = await db.Highlights.AsNoTracking()
            .Where(h => h.BookId == bookId)
            .OrderBy(h => h.Position)
            .ThenBy(h => h.CreatedAt)
            .Select(ToDetails)
            .ToListAsync(cancellationToken);

        return TypedResults.Ok(highlights);
    }

    private static async Task<Results<Created<HighlightDetails>, Ok<HighlightDetails>, NotFound, ValidationProblem, Conflict<ProblemDetails>>> CreateHighlight(
        Guid bookId, CreateHighlightRequest request, AppDbContext db, CancellationToken cancellationToken)
    {
        if (!await db.Books.AnyAsync(b => b.Id == bookId, cancellationToken))
        {
            return TypedResults.NotFound();
        }

        // Sent again with the same id: it was created already.
        if (request.Id is { } requestedId && requestedId != Guid.Empty
            && await db.Highlights.AsNoTracking().SingleOrDefaultAsync(h => h.Id == requestedId, cancellationToken) is { } existing)
        {
            return existing.BookId == bookId
                ? TypedResults.Ok(await FindAsync(db, existing.Id, cancellationToken))
                : ApiErrors.Conflict("A highlight with this id already exists.");
        }

        var file = await db.BookFiles.AsNoTracking()
            .SingleOrDefaultAsync(f => f.Id == request.FileId && f.BookId == bookId, cancellationToken);
        if (file is null)
        {
            return ApiErrors.Validation(nameof(CreateHighlightRequest.FileId), "This book has no such file.");
        }

        var highlight = new Highlight
        {
            Id = request.Id is { } id && id != Guid.Empty ? id : Guid.CreateVersion7(),
            BookId = bookId,
            FileId = file.Id,
            Format = file.Format,
            AnchoredSha256 = file.Sha256,
            Location = request.Location,
            Text = request.Text,
            Prefix = request.Prefix,
            Suffix = request.Suffix,
            Chapter = Blank(request.Chapter),
            PageLabel = Blank(request.PageLabel),
            Position = request.Position,
            Color = request.Color,
            Note = Blank(request.Note),
        };
        db.Highlights.Add(highlight);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ApiErrors.IsUniqueViolation(ex))
        {
            // The id belongs to another user's highlight (hidden by the query filter).
            return ApiErrors.Conflict("A highlight with this id already exists.");
        }

        return TypedResults.Created($"/api/books/{bookId}/highlights/{highlight.Id}", await FindAsync(db, highlight.Id, cancellationToken));
    }

    private static async Task<Results<Ok<HighlightDetails>, NotFound, Conflict<ProblemDetails>>> UpdateHighlight(
        Guid bookId, Guid id, UpdateHighlightRequest request, AppDbContext db, CancellationToken cancellationToken)
    {
        var highlight = await db.Highlights.SingleOrDefaultAsync(h => h.Id == id && h.BookId == bookId, cancellationToken);
        if (highlight is null)
        {
            return TypedResults.NotFound();
        }

        db.Entry(highlight).Property(h => h.Version).OriginalValue = request.Version.Value;
        highlight.Color = request.Color;
        highlight.Note = Blank(request.Note);
        db.Entry(highlight).Property(h => h.UpdatedAt).IsModified = true;

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return ApiErrors.ChangedElsewhere("highlight");
        }

        return TypedResults.Ok(await FindAsync(db, id, cancellationToken));
    }

    private static async Task<Results<Ok<HighlightDetails>, NotFound, ValidationProblem>> ReanchorHighlight(
        Guid bookId, Guid id, ReanchorHighlightRequest request, AppDbContext db, CancellationToken cancellationToken)
    {
        var highlight = await db.Highlights.SingleOrDefaultAsync(h => h.Id == id && h.BookId == bookId, cancellationToken);
        if (highlight is null)
        {
            return TypedResults.NotFound();
        }

        var file = await db.BookFiles.AsNoTracking()
            .SingleOrDefaultAsync(f => f.Id == request.FileId && f.BookId == bookId, cancellationToken);
        if (file is null || file.Format != highlight.Format)
        {
            return ApiErrors.Validation(nameof(ReanchorHighlightRequest.FileId), "Highlights can only move to the book's file in the same format.");
        }

        highlight.FileId = file.Id;
        highlight.AnchoredSha256 = file.Sha256;
        highlight.IsMissing = !request.Found;
        if (request.Found)
        {
            highlight.Location = request.Location;
            highlight.Chapter = Blank(request.Chapter) ?? highlight.Chapter;
            highlight.PageLabel = Blank(request.PageLabel);
            highlight.Position = request.Position ?? highlight.Position;
        }

        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Ok(await FindAsync(db, id, cancellationToken));
    }

    private static async Task<Results<NoContent, NotFound>> DeleteHighlight(
        Guid bookId, Guid id, AppDbContext db, CancellationToken cancellationToken)
    {
        var highlight = await db.Highlights.SingleOrDefaultAsync(h => h.Id == id && h.BookId == bookId, cancellationToken);
        if (highlight is null)
        {
            return TypedResults.NotFound();
        }

        db.Highlights.Remove(highlight);
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.NoContent();
    }

    private static string Blank(string value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static async Task<HighlightDetails> FindAsync(AppDbContext db, Guid id, CancellationToken cancellationToken) =>
        await db.Highlights.AsNoTracking().Where(h => h.Id == id).Select(ToDetails).SingleAsync(cancellationToken);

    private static readonly Expression<Func<Highlight, HighlightDetails>> ToDetails = h => new HighlightDetails(
        h.Id,
        h.BookId,
        h.FileId,
        h.Format,
        h.Location,
        h.Text,
        h.Prefix,
        h.Suffix,
        h.Chapter,
        h.PageLabel,
        h.Position,
        h.Color,
        h.Note,
        h.FileId == null
            ? HighlightState.Missing
            : h.File.Sha256 != h.AnchoredSha256
                ? HighlightState.NeedsCheck
                : h.IsMissing ? HighlightState.Missing : HighlightState.Anchored,
        h.CreatedAt,
        h.UpdatedAt,
        h.Version);
}
