using System.Threading.Channels;
using BookWorm.Server.Data;
using BookWorm.Server.Storage;
using Microsoft.EntityFrameworkCore;

namespace BookWorm.Server.Notes;

public interface INotesExportQueue
{
    /// <summary>Asks for the user's exported notes to be brought up to date soon.</summary>
    void Enqueue(Guid userId);
}

/// <summary>
/// Keeps <c>notes/{userName}/</c> in sync with each user's notes and highlights: one Markdown file per
/// book that has any. Changes are batched for a moment, then the user's whole folder is compared
/// with the database and only files whose content changed are rewritten. Files not written by
/// BookWorm are never touched.
/// </summary>
public sealed class NotesExporter(
    IServiceScopeFactory scopeFactory,
    LibraryStorage storage,
    TimeProvider timeProvider,
    ILogger<NotesExporter> logger) : BackgroundService, INotesExportQueue
{
    private static readonly TimeSpan Debounce = TimeSpan.FromSeconds(2);

    private readonly Channel<Guid> _changes = Channel.CreateUnbounded<Guid>(new UnboundedChannelOptions { SingleReader = true });
    private readonly SemaphoreSlim _syncLock = new(1, 1);

    public void Enqueue(Guid userId) => _changes.Writer.TryWrite(userId);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await SyncAllAsync(stoppingToken);
        }
        catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
        {
            logger.LogError(ex, "Could not export notes at startup.");
        }

        var pending = new HashSet<Guid>();
        try
        {
            while (await _changes.Reader.WaitToReadAsync(stoppingToken))
            {
                await Task.Delay(Debounce, timeProvider, stoppingToken);
                while (_changes.Reader.TryRead(out var userId))
                {
                    pending.Add(userId);
                }

                foreach (var userId in pending)
                {
                    try
                    {
                        await SyncUserAsync(userId, stoppingToken);
                    }
                    catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                    {
                        logger.LogError(ex, "Could not export the notes of user {UserId}.", userId);
                    }
                }

                pending.Clear();
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down.
        }
    }

    public async Task SyncAllAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var userIds = await db.Users.AsNoTracking().Select(u => u.Id).ToListAsync(cancellationToken);
        foreach (var userId in userIds)
        {
            await SyncUserAsync(userId, cancellationToken);
        }
    }

    public async Task SyncUserAsync(Guid userId, CancellationToken cancellationToken)
    {
        await _syncLock.WaitAsync(cancellationToken);
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var userName = await db.Users.AsNoTracking()
                .Where(u => u.Id == userId)
                .Select(u => u.UserName)
                .SingleOrDefaultAsync(cancellationToken);
            if (userName is null)
            {
                return;
            }

            // No signed-in user here, so the per-user filters are replaced by an explicit condition.
            var books = await NotesMarkdown.LoadAsync(
                db.Books.IgnoreQueryFilters().Where(b => b.UserId == userId && (b.Notes != null || b.Highlights.Any())),
                cancellationToken);

            var names = NotesMarkdown.FileNames(books);
            var expected = books.ToDictionary(b => names[b.Id], NotesMarkdown.Render, StringComparer.Ordinal);
            var folder = UserFolder(userId, userName);

            if (expected.Count > 0)
            {
                Directory.CreateDirectory(folder);
            }

            foreach (var (name, content) in expected)
            {
                var path = Path.Combine(folder, name);
                if (File.Exists(path) && await File.ReadAllTextAsync(path, cancellationToken) == content)
                {
                    continue;
                }

                var temp = Path.Combine(folder, $".{Guid.NewGuid():N}.tmp");
                await File.WriteAllTextAsync(temp, content, cancellationToken);
                File.Move(temp, path, overwrite: true);
            }

            if (!Directory.Exists(folder))
            {
                return;
            }

            foreach (var path in Directory.EnumerateFiles(folder, "*.md"))
            {
                if (!expected.ContainsKey(Path.GetFileName(path))
                    && NotesMarkdown.IsManaged(await File.ReadAllTextAsync(path, cancellationToken)))
                {
                    File.Delete(path);
                }
            }
        }
        finally
        {
            _syncLock.Release();
        }
    }

    public void DeleteUserFolder(Guid userId, string userName) =>
        LibraryStorage.TryDeleteDirectory(UserFolder(userId, userName));

    private string UserFolder(Guid userId, string userName) =>
        Path.Combine(storage.NotesRoot, NotesMarkdown.SafeName(userName, userId.ToString()));
}
