using System.Text.RegularExpressions;
using BookWorm.Contracts;
using BookWorm.Mobile.Core.Auth;
using BookWorm.Mobile.Core.Offline;

namespace BookWorm.Mobile.Core.WebView;

/// <summary>An answer to a request of the web view.</summary>
public sealed record WebViewResponse(int StatusCode, string ReasonPhrase, string ContentType, Stream Body);

/// <summary>
/// Answers the web view's own API requests: book files for the reader and cover images
/// (<c>&lt;img src="api/…"&gt;</c>). Those go to the app's origin, with neither the server's address
/// nor the app's token, so the app answers them: from the downloaded copy if there is one, else by
/// fetching them from the server with the token.
/// </summary>
/// <param name="server">An HttpClient straight to the server, with the token (as for <see cref="SyncService"/>).</param>
public sealed partial class WebViewFiles(OfflineStore store, SessionManager session, IConnectivity connectivity, Func<HttpClient> server)
{
    /// <summary>Whether the web view's request is one for the app to answer.</summary>
    public static bool Handles(string method, Uri uri) =>
        method.Equals("GET", StringComparison.OrdinalIgnoreCase) && uri.AbsolutePath.StartsWith("/api/", StringComparison.Ordinal);

    /// <summary>The downloaded copy of a book file or cover, or null when it isn't downloaded.</summary>
    public WebViewResponse FindDownloaded(Uri uri) =>
        FindDownloadedPath(uri) is { } local ? new WebViewResponse(200, "OK", local.ContentType, File.OpenRead(local.Path)) : null;

    /// <summary>What the web view is told a fetched file is; it only matters for images, which browsers check themselves.</summary>
    public static string GuessContentType(Uri uri) =>
        uri.AbsolutePath.EndsWith("/cover", StringComparison.Ordinal) ? "image/jpeg" : "application/octet-stream";

    /// <summary>
    /// Fetches the file from the server. The web view is answered before this finishes, so failures
    /// (offline, not found) come back as an empty body, which the page shows as a file that can't be opened.
    /// </summary>
    public async Task<Stream> FetchAsync(Uri uri, CancellationToken cancellationToken)
    {
        if (!connectivity.IsOnline)
        {
            return Stream.Null;
        }

        var http = server();
        try
        {
            var response = await http.GetAsync(uri.PathAndQuery.TrimStart('/'), HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                response.Dispose();
                http.Dispose();
                return Stream.Null;
            }

            var body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            return new OwningStream(body, response, http);
        }
        catch (HttpRequestException)
        {
            http.Dispose();
            return Stream.Null;
        }
    }

    private (string Path, string ContentType)? FindDownloadedPath(Uri uri)
    {
        if (session.User?.Id is not { } userId || DownloadablePath().Match(uri.AbsolutePath) is not { Success: true } match)
        {
            return null;
        }

        var bookId = Guid.Parse(match.Groups["book"].Value);
        if (match.Groups["file"].Success)
        {
            var fileId = Guid.Parse(match.Groups["file"].Value);
            var path = store.FindBookFile(userId, bookId, fileId);
            var format = store.Read<DownloadRecord>(userId, bookId, OfflineStore.FileJson)?.File.Format;
            return path is null || format is null ? null : (path, BookFormats.ContentType(format.Value));
        }

        return store.FindCover(userId, bookId) is { } cover ? (cover, ImageType(cover)) : null;
    }

    /// <summary>Covers are JPEG, PNG, WebP or GIF; the web view needs to be told which.</summary>
    private static string ImageType(string path)
    {
        Span<byte> header = stackalloc byte[12];
        using (var stream = File.OpenRead(path))
        {
            header = header[..stream.ReadAtLeast(header, header.Length, throwOnEndOfStream: false)];
        }

        return header switch
        {
            [0x89, 0x50, 0x4E, 0x47, ..] => "image/png",
            [0x47, 0x49, 0x46, ..] => "image/gif",
            [0x52, 0x49, 0x46, 0x46, _, _, _, _, 0x57, 0x45, 0x42, 0x50, ..] => "image/webp",
            _ => "image/jpeg",
        };
    }

    [GeneratedRegex(@"^/api/books/(?<book>[0-9a-fA-F-]{36})/(files/(?<file>[0-9a-fA-F-]{36})|cover)$")]
    private static partial Regex DownloadablePath();

    /// <summary>The response body, which also disposes the response and client once read.</summary>
    private sealed class OwningStream(Stream inner, IDisposable response, IDisposable client) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            inner.ReadAsync(buffer, offset, count, cancellationToken);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            inner.ReadAsync(buffer, cancellationToken);

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
                response.Dispose();
                client.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
