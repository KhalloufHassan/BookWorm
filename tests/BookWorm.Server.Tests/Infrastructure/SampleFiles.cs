using System.IO.Compression;
using System.Text;
using BookWorm.Contracts;
using BookWorm.UI.Api;

namespace BookWorm.Server.Tests.Infrastructure;

/// <summary>Tiny but structurally valid files in each supported format.</summary>
internal static class SampleFiles
{
    public static byte[] For(BookFormat format, string text = "It was a dark and stormy night.") => format switch
    {
        BookFormat.Epub => Epub(text),
        BookFormat.Pdf => Pdf(text),
        BookFormat.Mobi or BookFormat.Azw3 => Mobi(text),
        BookFormat.Fb2 => Fb2(text),
        BookFormat.Cbz => Cbz(),
        _ => throw new ArgumentOutOfRangeException(nameof(format)),
    };

    public static byte[] Epub(string text)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            Add(zip, "mimetype", "application/epub+zip", CompressionLevel.NoCompression);
            Add(zip, "META-INF/container.xml", """
                <?xml version="1.0"?>
                <container version="1.0" xmlns="urn:oasis:names:tc:opendocument:xmlns:container">
                  <rootfiles><rootfile full-path="OEBPS/content.opf" media-type="application/oebps-package+xml"/></rootfiles>
                </container>
                """);
            Add(zip, "OEBPS/content.opf", """
                <?xml version="1.0"?>
                <package xmlns="http://www.idpf.org/2007/opf" version="3.0" unique-identifier="id">
                  <metadata xmlns:dc="http://purl.org/dc/elements/1.1/">
                    <dc:identifier id="id">sample</dc:identifier><dc:title>Sample</dc:title><dc:language>en</dc:language>
                  </metadata>
                  <manifest><item id="c1" href="chapter.xhtml" media-type="application/xhtml+xml"/></manifest>
                  <spine><itemref idref="c1"/></spine>
                </package>
                """);
            Add(zip, "OEBPS/chapter.xhtml", $"""
                <?xml version="1.0"?>
                <html xmlns="http://www.w3.org/1999/xhtml"><head><title>One</title></head><body><p>{text}</p></body></html>
                """);
        }

        return buffer.ToArray();
    }

    public static byte[] Pdf(string text) => Encoding.ASCII.GetBytes($"%PDF-1.4\n% {text}\n1 0 obj << /Type /Catalog >> endobj\ntrailer << /Root 1 0 R >>\n%%EOF\n");

    public static byte[] Mobi(string text)
    {
        var bytes = new byte[128];
        Encoding.ASCII.GetBytes("Sample").CopyTo(bytes, 0);
        Encoding.ASCII.GetBytes("BOOKMOBI").CopyTo(bytes, 60);
        Encoding.ASCII.GetBytes(text[..Math.Min(text.Length, 40)]).CopyTo(bytes, 80);
        return bytes;
    }

    public static byte[] Fb2(string text) => Encoding.UTF8.GetBytes($"""
        <?xml version="1.0" encoding="UTF-8"?>
        <FictionBook xmlns="http://www.gribuser.ru/xml/fictionbook/2.0"><body><section><p>{text}</p></section></body></FictionBook>
        """);

    public static byte[] Cbz()
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = zip.CreateEntry("page-001.png");
            entry.LastWriteTime = FixedTime;
            using var page = entry.Open();
            page.Write(Png);
        }

        return buffer.ToArray();
    }

    /// <summary>A 1×1 PNG.</summary>
    public static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8/5+hHgAHggJ/PchI7wAAAABJRU5ErkJggg==");

    public static Task<BookFileDetails> UploadSampleAsync(this BookWormApiClient api, Guid bookId, BookFormat format, string text = "It was a dark and stormy night.")
    {
        var extension = BookFormats.Extensions(format)[0];
        return api.UploadFileAsync(bookId, format, new MemoryStream(For(format, text)), $"sample{extension}");
    }

    /// <summary>Zip entries carry a timestamp; a fixed one makes the same sample produce the same bytes every time.</summary>
    private static readonly DateTimeOffset FixedTime = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private static void Add(ZipArchive zip, string name, string content, CompressionLevel level = CompressionLevel.Optimal)
    {
        var entry = zip.CreateEntry(name, level);
        entry.LastWriteTime = FixedTime;
        using var stream = entry.Open();
        stream.Write(Encoding.UTF8.GetBytes(content));
    }
}
