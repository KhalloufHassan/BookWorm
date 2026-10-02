using BookWorm.Contracts;

namespace BookWorm.UI.Services;

/// <summary>A book downloaded to this device for reading offline.</summary>
/// <param name="UpdateAvailable">The file on the server changed since it was downloaded.</param>
/// <param name="PendingChanges">Reading progress, highlights and notes made offline that aren't on the server yet.</param>
public sealed record DownloadedBook(
    Guid BookId,
    string Title,
    IReadOnlyList<AuthorRef> Authors,
    long? CoverVersion,
    BookFormat Format,
    long SizeBytes,
    double? Progress,
    DateTimeOffset DownloadedAt,
    bool UpdateAvailable,
    int PendingChanges);

/// <summary>
/// Books downloaded for offline reading, in the mobile app. The web app doesn't support it
/// (<see cref="IsSupported"/> is false and the rest is never called).
/// </summary>
public interface IOfflineLibrary
{
    bool IsSupported { get; }

    /// <summary>Whether the server can be reached right now.</summary>
    bool IsOnline { get; }

    /// <summary>Downloads, connectivity or pending changes changed.</summary>
    event Action Changed;

    Task<IReadOnlyList<DownloadedBook>> GetDownloadsAsync();

    /// <summary>The download of a book, or null when it isn't downloaded.</summary>
    Task<DownloadedBook> GetDownloadAsync(Guid bookId);

    /// <summary>Downloads (or updates) the book's file for reading offline. Progress goes from 0 to 1.</summary>
    Task DownloadAsync(Guid bookId, IProgress<double> progress, CancellationToken cancellationToken);

    Task RemoveAsync(Guid bookId);
}

/// <summary>For hosts without offline reading (the web app).</summary>
public sealed class NoOfflineLibrary : IOfflineLibrary
{
    public bool IsSupported => false;

    public bool IsOnline => true;

    public event Action Changed { add { } remove { } }

    public Task<IReadOnlyList<DownloadedBook>> GetDownloadsAsync() => Task.FromResult<IReadOnlyList<DownloadedBook>>([]);

    public Task<DownloadedBook> GetDownloadAsync(Guid bookId) => Task.FromResult<DownloadedBook>(null);

    public Task DownloadAsync(Guid bookId, IProgress<double> progress, CancellationToken cancellationToken) =>
        throw new NotSupportedException("Offline reading is only available in the app.");

    public Task RemoveAsync(Guid bookId) => Task.CompletedTask;
}
