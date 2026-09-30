using System.IO.Compression;
using System.Text;
using BookWorm.Contracts;

namespace BookWorm.Server.Storage;

/// <summary>Checks that uploaded files really are what they claim to be, by looking at their content.</summary>
public static class FileInspector
{
    private static readonly string[] ImageExtensions = [".jpg", ".jpeg", ".png", ".gif", ".webp", ".avif", ".bmp"];

    public static bool IsFormat(string path, BookFormat format)
    {
        try
        {
            return format switch
            {
                BookFormat.Epub => ZipHas(path, entries => entries.Any(e => e.FullName == "META-INF/container.xml")),
                BookFormat.Cbz => ZipHas(path, entries => entries.Any(e =>
                    ImageExtensions.Contains(Path.GetExtension(e.FullName).ToLowerInvariant()))),
                BookFormat.Pdf => HeaderContains(path, 1024, "%PDF-"u8),
                BookFormat.Mobi or BookFormat.Azw3 => HasPalmDatabaseType(path, "BOOKMOBI"u8),
                BookFormat.Fb2 => HeaderContains(path, 8192, "<FictionBook"u8),
                _ => false,
            };
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException)
        {
            return false;
        }
    }

    /// <summary>The MIME type of a cover image, or null when it isn't a JPEG, PNG, WebP or GIF image.</summary>
    public static string DetectImageType(ReadOnlySpan<byte> header) => header switch
    {
        [0xFF, 0xD8, 0xFF, ..] => "image/jpeg",
        [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, ..] => "image/png",
        [0x47, 0x49, 0x46, 0x38, 0x37 or 0x39, 0x61, ..] => "image/gif",
        [0x52, 0x49, 0x46, 0x46, _, _, _, _, 0x57, 0x45, 0x42, 0x50, ..] => "image/webp",
        _ => null,
    };

    public static async Task<string> DetectImageTypeAsync(string path, CancellationToken cancellationToken)
    {
        var header = new byte[12];
        await using var stream = File.OpenRead(path);
        var read = await stream.ReadAtLeastAsync(header, header.Length, throwOnEndOfStream: false, cancellationToken);
        return DetectImageType(header.AsSpan(0, read));
    }

    private static bool ZipHas(string path, Func<IReadOnlyCollection<ZipArchiveEntry>, bool> check)
    {
        using var zip = ZipFile.OpenRead(path);
        return check(zip.Entries);
    }

    private static bool HeaderContains(string path, int length, ReadOnlySpan<byte> marker)
    {
        using var stream = File.OpenRead(path);
        var header = new byte[length];
        var read = stream.ReadAtLeast(header, length, throwOnEndOfStream: false);
        return header.AsSpan(0, read).IndexOf(marker) >= 0;
    }

    /// <summary>MOBI and KF8 (AZW3) files are Palm databases with type and creator "BOOKMOBI" at offset 60.</summary>
    private static bool HasPalmDatabaseType(string path, ReadOnlySpan<byte> type)
    {
        using var stream = File.OpenRead(path);
        var header = new byte[68];
        return stream.ReadAtLeast(header, header.Length, throwOnEndOfStream: false) == header.Length
            && header.AsSpan(60, 8).SequenceEqual(type);
    }

    /// <summary>A file name that is safe to put in a Content-Disposition header and on disk.</summary>
    public static string CleanFileName(string name, string fallback)
    {
        var cleaned = new StringBuilder();
        foreach (var c in Path.GetFileName(name ?? "").Trim())
        {
            cleaned.Append(char.IsControl(c) || Path.GetInvalidFileNameChars().Contains(c) || c is '"' or '\\' or '/' ? '_' : c);
        }

        var result = cleaned.ToString().Trim(' ', '.');
        if (result.Length == 0)
        {
            return fallback;
        }

        return result.Length <= ApiLimits.FileNameMaxLength ? result : result[..ApiLimits.FileNameMaxLength];
    }
}
