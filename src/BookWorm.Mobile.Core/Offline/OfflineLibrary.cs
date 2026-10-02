using System.Security.Cryptography;
using BookWorm.Contracts;
using BookWorm.Mobile.Core.Auth;
using BookWorm.UI.Api;
using BookWorm.UI.Services;

namespace BookWorm.Mobile.Core.Offline;

/// <summary>Downloading books for offline reading, and listing and removing downloads.</summary>
/// <param name="server">An HttpClient straight to the server (with the token), as for <see cref="SyncService"/>.</param>
public sealed class OfflineLibrary : IOfflineLibrary, IDisposable
{
    private const int BufferSize = 81920;

    private readonly OfflineStore _store;
    private readonly OfflineQueue _queue;
    private readonly SessionManager _session;
    private readonly IConnectivity _connectivity;
    private readonly Func<HttpClient> _server;
    private readonly TimeProvider _timeProvider;

    public OfflineLibrary(
        OfflineStore store, OfflineQueue queue, SessionManager session, IConnectivity connectivity,
        SyncService sync, Func<HttpClient> server, TimeProvider timeProvider)
    {
        _store = store;
        _queue = queue;
        _session = session;
        _connectivity = connectivity;
        _server = server;
        _timeProvider = timeProvider;
        _connectivity.Changed += RaiseChanged;
        _queue.Changed += RaiseChanged;
        sync.Synced += RaiseChanged;
    }

    public bool IsSupported => true;

    public bool IsOnline => _connectivity.IsOnline;

    public event Action Changed;

    public Task<IReadOnlyList<DownloadedBook>> GetDownloadsAsync()
    {
        if (_session.User?.Id is not { } userId)
        {
            return Task.FromResult<IReadOnlyList<DownloadedBook>>([]);
        }

        var queued = _queue.ReadAll(userId).GroupBy(entry => entry.Change.BookId).ToDictionary(group => group.Key, group => group.Count());
        IReadOnlyList<DownloadedBook> downloads = _store.ListBooks(userId)
            .Select(bookId => Describe(userId, bookId, queued.GetValueOrDefault(bookId)))
            .Where(download => download is not null)
            .OrderBy(download => download.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        return Task.FromResult(downloads);
    }

    public Task<DownloadedBook> GetDownloadAsync(Guid bookId) =>
        Task.FromResult(_session.User?.Id is { } userId && _store.IsDownloaded(userId, bookId)
            ? Describe(userId, bookId, _queue.Count(userId, bookId))
            : null);

    public async Task DownloadAsync(Guid bookId, IProgress<double> progress, CancellationToken cancellationToken)
    {
        var userId = _session.User?.Id ?? throw new InvalidOperationException("Not signed in.");
        if (!_connectivity.IsOnline)
        {
            throw new OfflineException("You're offline. Connect to the internet to download books.");
        }

        using var http = _server();
        var api = new BookWormApiClient(http);
        var book = await api.GetBookAsync(bookId, cancellationToken);
        var current = await api.GetCurrentReadAsync(bookId, cancellationToken);

        // The file the reader would open: the one being read, else the best format for reading.
        var file = book.Files.FirstOrDefault(f => f.Id == current?.FileId)
            ?? book.Files.OrderBy(f => BookFormats.ReadingPreference(f.Format)).FirstOrDefault()
            ?? throw new InvalidOperationException("This book has no file to download.");

        var wasDownloaded = _store.IsDownloaded(userId, bookId);
        var target = _store.BookFilePath(userId, bookId, file.Format);
        var partial = target + ".partial";
        Directory.CreateDirectory(_store.BookFolder(userId, bookId));
        try
        {
            await DownloadFileAsync(http, BookWormApiClient.FileUrl(bookId, file.Id), partial, file, progress, cancellationToken);

            if (book.CoverVersion is { } coverVersion)
            {
                await DownloadCoverAsync(http, BookWormApiClient.CoverUrl(bookId, coverVersion), Path.Combine(_store.BookFolder(userId, bookId), OfflineStore.CoverFile), cancellationToken);
            }
            else
            {
                File.Delete(Path.Combine(_store.BookFolder(userId, bookId), OfflineStore.CoverFile));
            }

            _store.Write(userId, bookId, OfflineStore.BookJson, book);
            _store.Write(userId, bookId, OfflineStore.HighlightsJson, await api.GetHighlightsAsync(bookId, cancellationToken));
            _store.Write(userId, bookId, OfflineStore.ReadJson, current);

            // A file in another format from an earlier download isn't needed anymore.
            foreach (var other in BookFormats.All.Where(format => format != file.Format))
            {
                File.Delete(_store.BookFilePath(userId, bookId, other));
            }

            File.Move(partial, target, overwrite: true);
            _store.Write(userId, bookId, OfflineStore.FileJson, new DownloadRecord(file, _timeProvider.GetUtcNow()));
        }
        catch
        {
            File.Delete(partial);
            if (!wasDownloaded)
            {
                _store.Delete(userId, bookId);
            }

            throw;
        }

        progress?.Report(1);
        RaiseChanged();
    }

    public Task RemoveAsync(Guid bookId)
    {
        if (_session.User?.Id is { } userId)
        {
            _store.Delete(userId, bookId);
            _queue.RemoveBook(userId, bookId);
            RaiseChanged();
        }

        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _connectivity.Changed -= RaiseChanged;
        _queue.Changed -= RaiseChanged;
    }

    private DownloadedBook Describe(Guid userId, Guid bookId, int pendingChanges)
    {
        var record = _store.Read<DownloadRecord>(userId, bookId, OfflineStore.FileJson);
        var book = _store.Read<BookDetails>(userId, bookId, OfflineStore.BookJson);
        if (record is null || book is null)
        {
            return null;
        }

        var read = _store.Read<ReadDetails>(userId, bookId, OfflineStore.ReadJson);
        var onServer = book.Files.FirstOrDefault(f => f.Id == record.File.Id);
        return new DownloadedBook(
            bookId,
            book.Title,
            book.Authors,
            book.CoverVersion,
            record.File.Format,
            _store.SizeOf(userId, bookId),
            read?.Progress,
            record.DownloadedAt,
            UpdateAvailable: onServer is not null && onServer.Sha256 != record.File.Sha256,
            pendingChanges);
    }

    /// <summary>Streams the file to disk, checking it arrived intact (SHA-256) before it's used.</summary>
    private static async Task DownloadFileAsync(
        HttpClient http, string url, string path, BookFileDetails file, IProgress<double> progress, CancellationToken cancellationToken)
    {
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new ApiException(response.StatusCode, null, "The book file couldn't be downloaded.");
        }

        var total = response.Content.Headers.ContentLength ?? file.SizeBytes;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken))
        await using (var target = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize, useAsync: true))
        {
            var buffer = new byte[BufferSize];
            long received = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                hash.AppendData(buffer, 0, read);
                received += read;
                if (total > 0)
                {
                    progress?.Report(Math.Min(0.99, (double)received / total));
                }
            }
        }

        if (!string.Equals(Convert.ToHexStringLower(hash.GetHashAndReset()), file.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException("The downloaded file was damaged on the way. Try again.");
        }
    }

    private static async Task DownloadCoverAsync(HttpClient http, string url, string path, CancellationToken cancellationToken)
    {
        using var response = await http.GetAsync(url, cancellationToken);
        if (response.IsSuccessStatusCode)
        {
            var temp = path + ".tmp";
            await File.WriteAllBytesAsync(temp, await response.Content.ReadAsByteArrayAsync(cancellationToken), cancellationToken);
            File.Move(temp, path, overwrite: true);
        }
    }

    private void RaiseChanged() => Changed?.Invoke();
}
