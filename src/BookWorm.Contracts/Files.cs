namespace BookWorm.Contracts;

/// <summary>The ebook formats BookWorm can store and read. A book has at most one file per format.</summary>
public enum BookFormat
{
    Epub,
    Pdf,
    Mobi,
    Azw3,
    Fb2,
    Cbz,
}

/// <summary>A book file. Its content is downloaded from <c>GET /api/books/{bookId}/files/{id}</c>.</summary>
/// <param name="Sha256">SHA-256 of the content, lowercase hex. Changes when the file is replaced.</param>
/// <param name="BrowseLocation">Where you last were in this file while browsing it with no read in progress.</param>
/// <param name="BrowseProgress">How far into the book that was, from 0 to 1.</param>
public sealed record BookFileDetails(
    Guid Id,
    BookFormat Format,
    string FileName,
    long SizeBytes,
    string Sha256,
    DateTimeOffset UploadedAt,
    string BrowseLocation,
    double? BrowseProgress);

/// <summary>What each format is called, which file names it has, and how it is served.</summary>
public static class BookFormats
{
    public static IReadOnlyList<BookFormat> All { get; } = Enum.GetValues<BookFormat>();

    /// <summary>File extensions accepted in the upload pickers, e.g. ".epub,.pdf".</summary>
    public static string AcceptAttribute { get; } = string.Join(',', All.SelectMany(Extensions));

    public static string Label(BookFormat format) => format switch
    {
        BookFormat.Epub => "EPUB",
        BookFormat.Pdf => "PDF",
        BookFormat.Mobi => "MOBI",
        BookFormat.Azw3 => "AZW3",
        BookFormat.Fb2 => "FB2",
        BookFormat.Cbz => "CBZ",
        _ => format.ToString(),
    };

    public static IReadOnlyList<string> Extensions(BookFormat format) => format switch
    {
        BookFormat.Epub => [".epub"],
        BookFormat.Pdf => [".pdf"],
        BookFormat.Mobi => [".mobi", ".prc"],
        BookFormat.Azw3 => [".azw3", ".azw"],
        BookFormat.Fb2 => [".fb2"],
        BookFormat.Cbz => [".cbz"],
        _ => [],
    };

    public static string ContentType(BookFormat format) => format switch
    {
        BookFormat.Epub => "application/epub+zip",
        BookFormat.Pdf => "application/pdf",
        BookFormat.Mobi => "application/x-mobipocket-ebook",
        BookFormat.Azw3 => "application/vnd.amazon.ebook",
        BookFormat.Fb2 => "application/x-fictionbook+xml",
        BookFormat.Cbz => "application/vnd.comicbook+zip",
        _ => "application/octet-stream",
    };

    /// <summary>The format a file name suggests, or null when it isn't a supported ebook.</summary>
    public static BookFormat? FromFileName(string fileName)
    {
        var extension = Path.GetExtension(fileName ?? "").ToLowerInvariant();
        foreach (var format in All)
        {
            if (Extensions(format).Contains(extension))
            {
                return format;
            }
        }

        return null;
    }

    /// <summary>Order in which the reader picks a file when a book has several.</summary>
    public static int ReadingPreference(BookFormat format) => format switch
    {
        BookFormat.Epub => 0,
        BookFormat.Azw3 => 1,
        BookFormat.Mobi => 2,
        BookFormat.Fb2 => 3,
        BookFormat.Pdf => 4,
        BookFormat.Cbz => 5,
        _ => 9,
    };
}

public static class FileSizes
{
    /// <summary>"12.3 MB" and the like.</summary>
    public static string Describe(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} bytes",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):0.#} MB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):0.##} GB",
    };
}

/// <summary>The result of uploading a cover.</summary>
/// <param name="CoverVersion">See <see cref="BookSummary.CoverVersion"/>.</param>
public sealed record CoverDetails(long CoverVersion);
