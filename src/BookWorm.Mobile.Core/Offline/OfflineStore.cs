using System.Text.Json;
using BookWorm.Contracts;

namespace BookWorm.Mobile.Core.Offline;

/// <summary>
/// Downloaded books on the phone, as plain files (no database). One folder per user and book:
/// <code>
/// offline/{userId}/{bookId}/
///   book.json         BookDetails, as last seen on the server
///   highlights.json   the book's highlights
///   read.json         the read in progress (or the last one finished here), if any
///   file.json         DownloadRecord: which file was downloaded, and when
///   book.{ext}        the book file itself
///   cover.jpg         the cover, if the book has one
/// </code>
/// Each JSON file is written to a temporary file first and then moved into place, so a crash never
/// leaves half a file behind.
/// </summary>
public sealed class OfflineStore(string rootFolder)
{
    public const string BookJson = "book.json";
    public const string HighlightsJson = "highlights.json";
    public const string ReadJson = "read.json";
    public const string FileJson = "file.json";
    public const string CoverFile = "cover.jpg";

    private readonly object _writeLock = new();

    public string UserFolder(Guid userId) => Path.Combine(rootFolder, "offline", userId.ToString("N"));

    public string BookFolder(Guid userId, Guid bookId) => Path.Combine(UserFolder(userId), bookId.ToString("N"));

    public bool IsDownloaded(Guid userId, Guid bookId) => File.Exists(Path.Combine(BookFolder(userId, bookId), FileJson));

    public IReadOnlyList<Guid> ListBooks(Guid userId)
    {
        var folder = UserFolder(userId);
        if (!Directory.Exists(folder))
        {
            return [];
        }

        return Directory.EnumerateDirectories(folder)
            .Select(path => Guid.TryParseExact(Path.GetFileName(path), "N", out var id) ? id : Guid.Empty)
            .Where(id => id != Guid.Empty && IsDownloaded(userId, id))
            .ToList();
    }

    public T Read<T>(Guid userId, Guid bookId, string name) where T : class
    {
        var path = Path.Combine(BookFolder(userId, bookId), name);
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path), BookWormJson.Options) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public void Write<T>(Guid userId, Guid bookId, string name, T value)
    {
        var folder = BookFolder(userId, bookId);
        var path = Path.Combine(folder, name);
        lock (_writeLock)
        {
            if (value is null)
            {
                File.Delete(path);
                return;
            }

            Directory.CreateDirectory(folder);
            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(value, BookWormJson.Options));
            File.Move(temp, path, overwrite: true);
        }
    }

    /// <summary>Changes a JSON file under a lock, so concurrent changes (reader and sync) don't overwrite each other.</summary>
    public void Update<T>(Guid userId, Guid bookId, string name, Func<T, T> change) where T : class
    {
        lock (_writeLock)
        {
            Write(userId, bookId, name, change(Read<T>(userId, bookId, name)));
        }
    }

    /// <summary>Where the book file is kept, whether or not it's there yet.</summary>
    public string BookFilePath(Guid userId, Guid bookId, BookFormat format) =>
        Path.Combine(BookFolder(userId, bookId), "book" + BookFormats.Extensions(format)[0]);

    /// <summary>The local copy of a book file, or null when that file isn't the one downloaded.</summary>
    public string FindBookFile(Guid userId, Guid bookId, Guid fileId)
    {
        var record = Read<DownloadRecord>(userId, bookId, FileJson);
        if (record?.File.Id != fileId)
        {
            return null;
        }

        var path = BookFilePath(userId, bookId, record.File.Format);
        return File.Exists(path) ? path : null;
    }

    public string FindCover(Guid userId, Guid bookId)
    {
        var path = Path.Combine(BookFolder(userId, bookId), CoverFile);
        return IsDownloaded(userId, bookId) && File.Exists(path) ? path : null;
    }

    public long SizeOf(Guid userId, Guid bookId)
    {
        var folder = new DirectoryInfo(BookFolder(userId, bookId));
        return folder.Exists ? folder.EnumerateFiles().Sum(file => file.Length) : 0;
    }

    public void Delete(Guid userId, Guid bookId)
    {
        var folder = BookFolder(userId, bookId);
        if (Directory.Exists(folder))
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
