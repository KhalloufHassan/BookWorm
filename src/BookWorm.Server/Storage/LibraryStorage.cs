using System.Buffers;
using System.Security.Cryptography;
using Microsoft.Extensions.Options;

namespace BookWorm.Server.Storage;

public sealed class StorageOptions
{
    public const string Section = "Storage";

    /// <summary>Folder for book files, covers and exported notes. Relative paths are from the app folder.</summary>
    public string DataPath { get; set; } = "data";

    /// <summary>Where the Markdown notes are exported; defaults to a <c>notes</c> folder inside <see cref="DataPath"/>.</summary>
    public string NotesPath { get; set; }

    public int MaxUploadMegabytes { get; set; } = 500;
}

/// <summary>
/// Where everything that isn't in the database lives on disk:
/// <code>
/// books/{userId}/{bookId}/{fileId}   book files (the format is in the database)
/// covers/{userId}/{bookId}           cover images (the type is detected when served)
/// notes/{userName}/*.md              exported notes
/// tmp/                               uploads in progress
/// </code>
/// Paths are built from ids only, never from names sent by clients.
/// </summary>
public sealed class LibraryStorage
{
    public LibraryStorage(IOptions<StorageOptions> options, IHostEnvironment environment)
    {
        var settings = options.Value;
        Root = Path.GetFullPath(settings.DataPath, environment.ContentRootPath);
        NotesRoot = string.IsNullOrWhiteSpace(settings.NotesPath)
            ? Path.Combine(Root, "notes")
            : Path.GetFullPath(settings.NotesPath, environment.ContentRootPath);
        MaxUploadBytes = Math.Max(1, settings.MaxUploadMegabytes) * 1024L * 1024L;
    }

    public string Root { get; }
    public string BooksRoot => Path.Combine(Root, "books");
    public string CoversRoot => Path.Combine(Root, "covers");
    public string NotesRoot { get; }
    public string TempRoot => Path.Combine(Root, "tmp");
    public long MaxUploadBytes { get; }

    public string BookFolder(Guid userId, Guid bookId) => Path.Combine(BooksRoot, userId.ToString(), bookId.ToString());

    public string BookFilePath(Guid userId, Guid bookId, Guid fileId) => Path.Combine(BookFolder(userId, bookId), fileId.ToString());

    public string CoverPath(Guid userId, Guid bookId) => Path.Combine(CoversRoot, userId.ToString(), bookId.ToString());

    /// <summary>Clears uploads left over from a previous run.</summary>
    public void Initialize()
    {
        Directory.CreateDirectory(Root);
        if (Directory.Exists(TempRoot))
        {
            Directory.Delete(TempRoot, recursive: true);
        }

        Directory.CreateDirectory(TempRoot);
    }

    /// <summary>
    /// Streams a request body into a temporary file, hashing it on the way. Throws
    /// <see cref="FileTooLargeException"/> once it grows past <paramref name="maxBytes"/>.
    /// </summary>
    public async Task<ReceivedFile> ReceiveAsync(Stream body, long maxBytes, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(TempRoot);
        var path = Path.Combine(TempRoot, Guid.NewGuid().ToString("N"));
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(128 * 1024);
        long size = 0;
        try
        {
            await using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.Asynchronous))
            {
                int read;
                while ((read = await body.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    size += read;
                    if (size > maxBytes)
                    {
                        throw new FileTooLargeException(maxBytes);
                    }

                    hash.AppendData(buffer, 0, read);
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                }
            }

            return new ReceivedFile(path, size, Convert.ToHexStringLower(hash.GetHashAndReset()));
        }
        catch
        {
            TryDeleteFile(path);
            throw;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public void DeleteBook(Guid userId, Guid bookId)
    {
        TryDeleteDirectory(BookFolder(userId, bookId));
        TryDeleteFile(CoverPath(userId, bookId));
    }

    public void DeleteUser(Guid userId)
    {
        TryDeleteDirectory(Path.Combine(BooksRoot, userId.ToString()));
        TryDeleteDirectory(Path.Combine(CoversRoot, userId.ToString()));
    }

    public static void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort: an orphaned file only costs disk space.
        }
    }

    public static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort, as above.
        }
    }
}

/// <summary>An upload waiting in the temp folder. Deleted on dispose unless moved into place.</summary>
public sealed class ReceivedFile(string path, long size, string sha256) : IDisposable
{
    private bool _moved;

    public string Path { get; } = path;
    public long Size { get; } = size;
    public string Sha256 { get; } = sha256;

    /// <summary>Atomically puts the file at its final location, replacing what was there.</summary>
    public void MoveTo(string destination)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(destination));
        File.Move(Path, destination, overwrite: true);
        _moved = true;
    }

    public void Dispose()
    {
        if (!_moved)
        {
            LibraryStorage.TryDeleteFile(Path);
        }
    }
}

public sealed class FileTooLargeException(long maxBytes)
    : Exception($"The file is larger than the {maxBytes / (1024 * 1024)} MB limit.")
{
    public long MaxBytes { get; } = maxBytes;
}
