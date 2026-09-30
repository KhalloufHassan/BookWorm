using BookWorm.Contracts;
using BookWorm.Server.Auth;
using BookWorm.Server.Data;
using BookWorm.Server.Storage;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Net.Http.Headers;

namespace BookWorm.Server.Api;

/// <summary>
/// Book files and covers. Uploads are the raw file as the request body (not a form), so large
/// books stream straight to disk.
/// </summary>
internal static class FileEndpoints
{
    public static void MapFileEndpoints(this IEndpointRouteBuilder api)
    {
        var book = api.MapGroup("/books/{bookId:guid}").WithTags("Files");

        book.MapPut("/files/{format}", UploadFile)
            .WithSummary("Upload the book's file in a format (epub, pdf, mobi, azw3, fb2 or cbz), replacing any file in that format.");
        book.MapGet("/files/{fileId:guid}", DownloadFile)
            .WithSummary("Download a book file. Supports range requests; add ?download=true to save it with its original name.");
        book.MapPut("/files/{fileId:guid}/position", SaveBrowsePosition).WithValidation<BrowsePositionRequest>()
            .WithSummary("Save where you are in a file while browsing it with no read in progress.");
        book.MapDelete("/files/{fileId:guid}", DeleteFile)
            .WithSummary("Delete a book file. Its highlights are kept, and come back if a file in that format is uploaded again.");

        book.MapGet("/cover", GetCover).WithSummary("The book's cover image.");
        book.MapPut("/cover", UploadCover)
            .WithSummary("Upload a cover image (JPEG, PNG, WebP or GIF, up to 10 MB).");
        book.MapDelete("/cover", DeleteCover).WithSummary("Remove the book's cover.");
    }

    private static async Task<Results<Created<BookFileDetails>, Ok<BookFileDetails>, NotFound, ValidationProblem, ProblemHttpResult>> UploadFile(
        Guid bookId,
        string format,
        [FromQuery] string fileName,
        HttpContext context,
        AppDbContext db,
        LibraryStorage storage,
        ICurrentUser currentUser,
        TimeProvider timeProvider,
        ILogger<LibraryStorage> logger,
        CancellationToken cancellationToken)
    {
        if (!Enum.TryParse<BookFormat>(format, ignoreCase: true, out var bookFormat) || !Enum.IsDefined(bookFormat))
        {
            return ApiErrors.Validation("format", "Supported formats are EPUB, PDF, MOBI, AZW3, FB2 and CBZ.");
        }

        if (!await db.Books.AnyAsync(b => b.Id == bookId, cancellationToken))
        {
            return TypedResults.NotFound();
        }

        using var received = await ReceiveAsync(context, storage, storage.MaxUploadBytes, cancellationToken);
        if (received.Problem is { } problem)
        {
            return problem;
        }

        var upload = received.File;
        if (upload.Size == 0 || !FileInspector.IsFormat(upload.Path, bookFormat))
        {
            return ApiErrors.Validation("file", $"This isn't a valid {BookFormats.Label(bookFormat)} file.");
        }

        // PostgreSQL keeps microseconds; trimming here makes this response match later reads.
        var now = timeProvider.GetUtcNow();
        now = now.AddTicks(-(now.Ticks % TimeSpan.TicksPerMicrosecond));
        var cleanName = FileInspector.CleanFileName(fileName, $"book{BookFormats.Extensions(bookFormat)[0]}");
        var file = await db.BookFiles.SingleOrDefaultAsync(f => f.BookId == bookId && f.Format == bookFormat, cancellationToken);
        var isNew = file is null;
        if (file is null)
        {
            file = new BookFile { BookId = bookId, Format = bookFormat, FileName = cleanName, Sha256 = upload.Sha256 };
            db.BookFiles.Add(file);

            // Highlights whose file was deleted come back with a new file in the same format; the reader
            // then looks for their text in it.
            var orphans = await db.Highlights
                .Where(h => h.BookId == bookId && h.FileId == null && h.Format == bookFormat)
                .ToListAsync(cancellationToken);
            foreach (var highlight in orphans)
            {
                highlight.File = file;
            }
        }

        file.FileName = cleanName;
        file.SizeBytes = upload.Size;
        file.Sha256 = upload.Sha256;
        file.UploadedAt = now;

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ApiErrors.IsUniqueViolation(ex))
        {
            return TypedResults.Problem(
                "Another upload of this format finished at the same time. Reload the book and try again.",
                statusCode: StatusCodes.Status409Conflict);
        }

        upload.MoveTo(storage.BookFilePath(currentUser.RequireUserId(), bookId, file.Id));
        logger.LogInformation("Stored {Format} file {FileId} ({Size} bytes) for book {BookId}.", bookFormat, file.Id, upload.Size, bookId);

        var details = ToDetails(file);
        return isNew ? TypedResults.Created($"/api/books/{bookId}/files/{file.Id}", details) : TypedResults.Ok(details);
    }

    private static async Task<Results<PhysicalFileHttpResult, NotFound, ProblemHttpResult>> DownloadFile(
        Guid bookId,
        Guid fileId,
        [FromQuery] bool? download,
        HttpContext context,
        AppDbContext db,
        LibraryStorage storage,
        ICurrentUser currentUser,
        CancellationToken cancellationToken)
    {
        var file = await db.BookFiles.AsNoTracking()
            .SingleOrDefaultAsync(f => f.Id == fileId && f.BookId == bookId, cancellationToken);
        if (file is null)
        {
            return TypedResults.NotFound();
        }

        var path = storage.BookFilePath(currentUser.RequireUserId(), bookId, fileId);
        if (!File.Exists(path))
        {
            return TypedResults.Problem("The file is missing on the server. Upload it again.", statusCode: StatusCodes.Status404NotFound);
        }

        // Book content (FB2 is XML, EPUBs are HTML) must never run as a page of this site.
        context.Response.Headers.ContentSecurityPolicy = "sandbox; default-src 'none'";
        context.Response.Headers.CacheControl = "private, no-cache";

        return TypedResults.PhysicalFile(
            path,
            BookFormats.ContentType(file.Format),
            download == true ? file.FileName : null,
            file.UploadedAt,
            new EntityTagHeaderValue($"\"{file.Sha256}\""),
            enableRangeProcessing: true);
    }

    /// <summary>A bulk update: browsing isn't an edit of the file, so it doesn't touch its UpdatedAt.</summary>
    private static async Task<Results<NoContent, NotFound>> SaveBrowsePosition(
        Guid bookId, Guid fileId, BrowsePositionRequest request, AppDbContext db, CancellationToken cancellationToken)
    {
        var updated = await db.BookFiles
            .Where(f => f.Id == fileId && f.BookId == bookId)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(f => f.BrowseLocation, request.Location)
                .SetProperty(f => f.BrowseProgress, request.Progress),
                cancellationToken);
        return updated == 0 ? TypedResults.NotFound() : TypedResults.NoContent();
    }

    private static async Task<Results<NoContent, NotFound>> DeleteFile(
        Guid bookId, Guid fileId, AppDbContext db, LibraryStorage storage, ICurrentUser currentUser, CancellationToken cancellationToken)
    {
        var file = await db.BookFiles.SingleOrDefaultAsync(f => f.Id == fileId && f.BookId == bookId, cancellationToken);
        if (file is null)
        {
            return TypedResults.NotFound();
        }

        // The database keeps the highlights and reads (their file link becomes empty).
        db.BookFiles.Remove(file);
        await db.SaveChangesAsync(cancellationToken);

        LibraryStorage.TryDeleteFile(storage.BookFilePath(currentUser.RequireUserId(), bookId, fileId));
        return TypedResults.NoContent();
    }

    private static async Task<Results<PhysicalFileHttpResult, NotFound>> GetCover(
        Guid bookId,
        [FromQuery] long? v,
        HttpContext context,
        AppDbContext db,
        LibraryStorage storage,
        ICurrentUser currentUser,
        CancellationToken cancellationToken)
    {
        var coverUpdatedAt = await db.Books.AsNoTracking()
            .Where(b => b.Id == bookId)
            .Select(b => b.CoverUpdatedAt)
            .SingleOrDefaultAsync(cancellationToken);
        var path = storage.CoverPath(currentUser.RequireUserId(), bookId);
        if (coverUpdatedAt is null || !File.Exists(path))
        {
            return TypedResults.NotFound();
        }

        var contentType = await FileInspector.DetectImageTypeAsync(path, cancellationToken) ?? "application/octet-stream";

        // Versioned URLs never change, so browsers can keep them; unversioned ones are revalidated.
        context.Response.Headers.CacheControl = v is not null ? "private, max-age=31536000, immutable" : "private, no-cache";
        context.Response.Headers.ContentSecurityPolicy = "sandbox; default-src 'none'";

        return TypedResults.PhysicalFile(
            path,
            contentType,
            lastModified: coverUpdatedAt,
            entityTag: new EntityTagHeaderValue($"\"{Covers.Version(coverUpdatedAt)}\""));
    }

    private static async Task<Results<Ok<CoverDetails>, NotFound, ValidationProblem, ProblemHttpResult>> UploadCover(
        Guid bookId,
        HttpContext context,
        AppDbContext db,
        LibraryStorage storage,
        ICurrentUser currentUser,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var book = await db.Books.SingleOrDefaultAsync(b => b.Id == bookId, cancellationToken);
        if (book is null)
        {
            return TypedResults.NotFound();
        }

        using var received = await ReceiveAsync(context, storage, ApiLimits.CoverMaxBytes, cancellationToken);
        if (received.Problem is { } problem)
        {
            return problem;
        }

        var upload = received.File;
        if (await FileInspector.DetectImageTypeAsync(upload.Path, cancellationToken) is null)
        {
            return ApiErrors.Validation("file", "Covers must be JPEG, PNG, WebP or GIF images.");
        }

        upload.MoveTo(storage.CoverPath(currentUser.RequireUserId(), bookId));
        book.CoverUpdatedAt = timeProvider.GetUtcNow();
        await db.SaveChangesAsync(cancellationToken);

        return TypedResults.Ok(new CoverDetails(Covers.Version(book.CoverUpdatedAt).Value));
    }

    private static async Task<Results<NoContent, NotFound>> DeleteCover(
        Guid bookId, AppDbContext db, LibraryStorage storage, ICurrentUser currentUser, CancellationToken cancellationToken)
    {
        var book = await db.Books.SingleOrDefaultAsync(b => b.Id == bookId, cancellationToken);
        if (book is null)
        {
            return TypedResults.NotFound();
        }

        book.CoverUpdatedAt = null;
        await db.SaveChangesAsync(cancellationToken);
        LibraryStorage.TryDeleteFile(storage.CoverPath(currentUser.RequireUserId(), bookId));
        return TypedResults.NoContent();
    }

    /// <summary>
    /// Receives the request body into a temp file, answering 413 when it's over <paramref name="maxBytes"/>.
    /// </summary>
    internal static async Task<Upload> ReceiveAsync(HttpContext context, LibraryStorage storage, long maxBytes, CancellationToken cancellationToken)
    {
        if (context.Request.ContentLength > maxBytes)
        {
            return new Upload(null, TooLarge(maxBytes));
        }

        // Kestrel's default limit (30 MB) applies to the whole server; raise it for this request only.
        if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
        {
            limit.MaxRequestBodySize = maxBytes;
        }

        try
        {
            return new Upload(await storage.ReceiveAsync(context.Request.Body, maxBytes, cancellationToken), null);
        }
        catch (FileTooLargeException)
        {
            return new Upload(null, TooLarge(maxBytes));
        }
        catch (BadHttpRequestException ex) when (ex.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            return new Upload(null, TooLarge(maxBytes));
        }
    }

    private static ProblemHttpResult TooLarge(long maxBytes) => TypedResults.Problem(
        $"The file is too large. The limit is {FileSizes.Describe(maxBytes)}.",
        statusCode: StatusCodes.Status413PayloadTooLarge);

    internal static BookFileDetails ToDetails(BookFile file) =>
        new(file.Id, file.Format, file.FileName, file.SizeBytes, file.Sha256, file.UploadedAt, file.BrowseLocation, file.BrowseProgress);

    internal sealed record Upload(ReceivedFile File, ProblemHttpResult Problem) : IDisposable
    {
        public void Dispose() => File?.Dispose();
    }
}

internal static class Covers
{
    /// <summary>A number that changes whenever the cover does, for cache-busting cover URLs.</summary>
    public static long? Version(DateTimeOffset? coverUpdatedAt) => coverUpdatedAt?.ToUnixTimeMilliseconds();
}
