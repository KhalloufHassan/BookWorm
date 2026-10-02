using System.Linq.Expressions;
using BookWorm.Contracts;
using BookWorm.Server.Data;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace BookWorm.Server.Api;

internal static class ReadEndpoints
{
    public static void MapReadEndpoints(this IEndpointRouteBuilder api)
    {
        var reads = api.MapGroup("/books/{bookId:guid}/reads").WithTags("Reads");

        reads.MapGet("/", ListReads).WithSummary("List the reads of a book, most recent first.");
        reads.MapPost("/", CreateRead).WithValidation<CreateReadRequest>().WithSummary("Record a read of a book.");
        reads.MapPut("/{readId:guid}", UpdateRead).WithValidation<UpdateReadRequest>().WithSummary("Replace a read's details.");
        reads.MapDelete("/{readId:guid}", DeleteRead).WithSummary("Delete a read.");

        reads.MapGet("/current", GetCurrent)
            .WithSummary("The read in progress, or 204 No Content when there is none. The reader calls this when a book is opened.");
        reads.MapPost("/current", StartOrResume).Accepts<StartReadRequest>("application/json")
            .WithSummary("The read in progress, started now if there is none. The reader's \"Start reading\" button calls this.");
        reads.MapPut("/{readId:guid}/progress", SaveProgress).WithValidation<ReadProgressRequest>()
            .WithSummary("Save where the reader is, and extend the current reading session.");
        reads.MapPost("/{readId:guid}/finish", FinishRead).WithValidation<FinishReadRequest>()
            .WithSummary("Mark a read finished or skipped, optionally updating the book's status too.");

        api.MapGet("/reading", ListCurrentReads).WithTags("Reads")
            .WithSummary("Reads in progress, most recently opened first (for \"continue reading\").");
    }

    private static async Task<Results<Ok<List<ReadDetails>>, NotFound>> ListReads(
        Guid bookId, AppDbContext db, CancellationToken cancellationToken)
    {
        if (!await db.Books.AnyAsync(b => b.Id == bookId, cancellationToken))
        {
            return TypedResults.NotFound();
        }

        var reads = await db.Reads.AsNoTracking()
            .Where(r => r.BookId == bookId)
            .OrderBy(r => r.StartedAt == null)
            .ThenByDescending(r => r.StartedAt)
            .ThenByDescending(r => r.CreatedAt)
            .Select(ToDetails)
            .ToListAsync(cancellationToken);

        return TypedResults.Ok(reads);
    }

    private static async Task<Results<Created<ReadDetails>, NotFound>> CreateRead(
        Guid bookId, CreateReadRequest request, AppDbContext db, CancellationToken cancellationToken)
    {
        if (!await db.Books.AnyAsync(b => b.Id == bookId, cancellationToken))
        {
            return TypedResults.NotFound();
        }

        var read = new Read { BookId = bookId };
        Apply(request, read);

        db.Reads.Add(read);
        await db.SaveChangesAsync(cancellationToken);

        return TypedResults.Created($"/api/books/{bookId}/reads/{read.Id}", ToDetailsInMemory(read));
    }

    private static async Task<Results<Ok<ReadDetails>, NotFound, Conflict<ProblemDetails>>> UpdateRead(
        Guid bookId, Guid readId, UpdateReadRequest request, AppDbContext db, CancellationToken cancellationToken)
    {
        var read = await db.Reads.SingleOrDefaultAsync(r => r.Id == readId && r.BookId == bookId, cancellationToken);
        if (read is null)
        {
            return TypedResults.NotFound();
        }

        db.Entry(read).Property(r => r.Version).OriginalValue = request.Version.Value;
        Apply(request, read);
        db.Entry(read).Property(r => r.UpdatedAt).IsModified = true;

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return ApiErrors.ChangedElsewhere("read");
        }

        return TypedResults.Ok(ToDetailsInMemory(read));
    }

    private static async Task<Results<NoContent, NotFound>> DeleteRead(
        Guid bookId, Guid readId, AppDbContext db, CancellationToken cancellationToken)
    {
        var deleted = await db.Reads.Where(r => r.Id == readId && r.BookId == bookId).ExecuteDeleteAsync(cancellationToken);
        return deleted == 0 ? TypedResults.NotFound() : TypedResults.NoContent();
    }

    private static async Task<Results<Ok<ReadDetails>, NoContent, NotFound>> GetCurrent(
        Guid bookId, AppDbContext db, CancellationToken cancellationToken)
    {
        if (!await db.Books.AnyAsync(b => b.Id == bookId, cancellationToken))
        {
            return TypedResults.NotFound();
        }

        var current = await FindCurrentAsync(db, bookId, cancellationToken);
        return current is null ? TypedResults.NoContent() : TypedResults.Ok(current);
    }

    private static async Task<Results<Ok<ReadDetails>, Created<ReadDetails>, NotFound, Conflict<ProblemDetails>>> StartOrResume(
        Guid bookId, AppDbContext db, TimeProvider timeProvider, CancellationToken cancellationToken, [FromBody] StartReadRequest request = null)
    {
        if (!await db.Books.AnyAsync(b => b.Id == bookId, cancellationToken))
        {
            return TypedResults.NotFound();
        }

        // An app sending a read it started offline again: it exists already.
        var requestedId = request?.Id is { } id && id != Guid.Empty ? id : (Guid?)null;
        if (requestedId is not null
            && await db.Reads.AsNoTracking().Where(r => r.Id == requestedId).Select(ToDetails).SingleOrDefaultAsync(cancellationToken) is { } existing)
        {
            return existing.BookId == bookId ? TypedResults.Ok(existing) : ApiErrors.Conflict("A read with this id already exists.");
        }

        var current = await FindCurrentAsync(db, bookId, cancellationToken);
        if (current is not null)
        {
            return TypedResults.Ok(current);
        }

        var now = timeProvider.GetUtcNow();
        var started = request?.StartedAt?.ToUniversalTime() is { } startedAt && startedAt < now ? startedAt : now;
        var read = new Read
        {
            Id = requestedId ?? Guid.CreateVersion7(),
            BookId = bookId,
            Status = ReadStatus.CurrentlyReading,
            StartedAt = started,
            LastOpenedAt = started,
        };
        db.Reads.Add(read);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ApiErrors.IsUniqueViolation(ex))
        {
            // The id belongs to another user's read (hidden by the query filter).
            return ApiErrors.Conflict("A read with this id already exists.");
        }

        return TypedResults.Created($"/api/books/{bookId}/reads/{read.Id}", ToDetailsInMemory(read));
    }

    private static Task<ReadDetails> FindCurrentAsync(AppDbContext db, Guid bookId, CancellationToken cancellationToken) =>
        db.Reads.AsNoTracking()
            .Where(r => r.BookId == bookId && r.Status == ReadStatus.CurrentlyReading)
            .OrderByDescending(r => r.LastOpenedAt)
            .ThenByDescending(r => r.StartedAt)
            .ThenByDescending(r => r.CreatedAt)
            .Select(ToDetails)
            .FirstOrDefaultAsync(cancellationToken);

    private static async Task<Results<NoContent, NotFound, ValidationProblem>> SaveProgress(
        Guid bookId, Guid readId, ReadProgressRequest request, AppDbContext db, TimeProvider timeProvider, CancellationToken cancellationToken)
    {
        if (!await db.BookFiles.AnyAsync(f => f.Id == request.FileId && f.BookId == bookId, cancellationToken))
        {
            return ApiErrors.Validation(nameof(ReadProgressRequest.FileId), "This book has no such file.");
        }

        var now = timeProvider.GetUtcNow();

        // Saves sent later (after reading offline) carry their own time, which can't be in the future.
        var savedAt = request.SavedAt?.ToUniversalTime() is { } sent && sent < now ? sent : now;

        // A bulk update, so saving progress never conflicts with edits to the read's details. A save
        // older than the last one (e.g. queued offline while reading on another device) is skipped.
        var updated = await db.Reads
            .Where(r => r.Id == readId && r.BookId == bookId && (r.LastOpenedAt == null || r.LastOpenedAt <= savedAt))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(r => r.FileId, request.FileId)
                .SetProperty(r => r.Location, request.Location)
                .SetProperty(r => r.Progress, request.Progress)
                .SetProperty(r => r.LastOpenedAt, savedAt)
                .SetProperty(r => r.UpdatedAt, now),
                cancellationToken);
        if (updated == 0 && !await db.Reads.AnyAsync(r => r.Id == readId && r.BookId == bookId, cancellationToken))
        {
            return TypedResults.NotFound();
        }

        // The reading time counts even when the position was out of date.
        if (request.SessionId is { } sessionId && sessionId != Guid.Empty)
        {
            await SaveSessionAsync(db, readId, sessionId, request, savedAt, cancellationToken);
        }

        return TypedResults.NoContent();
    }

    /// <summary>Starts or extends a reading session up to <paramref name="now"/>, which is never in the future.</summary>
    private static async Task SaveSessionAsync(
        AppDbContext db, Guid readId, Guid sessionId, ReadProgressRequest request, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var session = await db.ReadingSessions.SingleOrDefaultAsync(s => s.Id == sessionId, cancellationToken);
        if (session is null)
        {
            var started = request.SessionStartedAt?.ToUniversalTime() ?? now;
            if (started > now || started < now - MaxSessionLength)
            {
                started = now;
            }

            db.ReadingSessions.Add(new ReadingSession
            {
                Id = sessionId,
                ReadId = readId,
                FileId = request.FileId,
                StartedAt = started,
                EndedAt = now,
                StartProgress = request.SessionStartProgress ?? request.Progress,
                EndProgress = request.Progress,
            });
        }
        else if (session.ReadId == readId && now - session.StartedAt <= MaxSessionLength)
        {
            if (now <= session.EndedAt)
            {
                return;
            }

            session.EndedAt = now;
            session.EndProgress = request.Progress;
        }
        else
        {
            return;
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ApiErrors.IsUniqueViolation(ex))
        {
            // Two saves of a brand-new session raced; the other one created it.
        }
    }

    /// <summary>Longer "sittings" are almost certainly a reader left open, so they stop counting.</summary>
    private static readonly TimeSpan MaxSessionLength = TimeSpan.FromHours(12);

    private static async Task<Results<Ok<ReadDetails>, NotFound>> FinishRead(
        Guid bookId, Guid readId, FinishReadRequest request, AppDbContext db, TimeProvider timeProvider, CancellationToken cancellationToken)
    {
        var read = await db.Reads.Include(r => r.Book).SingleOrDefaultAsync(r => r.Id == readId && r.BookId == bookId, cancellationToken);
        if (read is null)
        {
            return TypedResults.NotFound();
        }

        var finished = request.FinishedAt?.ToUniversalTime() ?? timeProvider.GetUtcNow();
        read.Status = request.Status;
        read.FinishedAt = read.StartedAt is { } started && finished < started ? started : finished;
        if (request.UpdateBookStatus)
        {
            read.Book.Status = request.Status == ReadStatus.Skipped ? BookStatus.Skipped : BookStatus.Finished;
        }

        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Ok(ToDetailsInMemory(read));
    }

    private static async Task<Ok<List<CurrentRead>>> ListCurrentReads(AppDbContext db, CancellationToken cancellationToken)
    {
        var reads = await db.Reads.AsNoTracking()
            .Where(r => r.Status == ReadStatus.CurrentlyReading)
            .OrderByDescending(r => r.LastOpenedAt != null)
            .ThenByDescending(r => r.LastOpenedAt)
            .ThenByDescending(r => r.StartedAt)
            .Take(12)
            .Select(r => new CurrentRead(
                new ReadDetails(r.Id, r.BookId, r.Status, r.StartedAt, r.FinishedAt, r.FileId, r.Location, r.Progress, r.LastOpenedAt, r.CreatedAt, r.UpdatedAt, r.Version),
                r.BookId,
                r.Book.Title,
                r.Book.Authors.OrderBy(ba => ba.Position).Select(ba => new AuthorRef(ba.Author.Id, ba.Author.Name)).ToList(),
                Covers.Version(r.Book.CoverUpdatedAt)))
            .AsSplitQuery()
            .ToListAsync(cancellationToken);

        // One entry per book: the most recently opened read.
        return TypedResults.Ok(reads.DistinctBy(r => r.BookId).ToList());
    }

    private static void Apply(CreateReadRequest request, Read read)
    {
        read.Status = request.Status;
        read.StartedAt = request.StartedAt?.ToUniversalTime();
        read.FinishedAt = request.FinishedAt?.ToUniversalTime();
    }

    internal static readonly Expression<Func<Read, ReadDetails>> ToDetails = r => new ReadDetails(
        r.Id, r.BookId, r.Status, r.StartedAt, r.FinishedAt, r.FileId, r.Location, r.Progress, r.LastOpenedAt, r.CreatedAt, r.UpdatedAt, r.Version);

    private static readonly Func<Read, ReadDetails> ToDetailsInMemory = ToDetails.Compile();
}
