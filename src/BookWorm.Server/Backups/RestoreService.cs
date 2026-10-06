using System.Text.Json;
using BookWorm.Contracts;
using BookWorm.Server.Data;
using BookWorm.Server.Storage;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace BookWorm.Server.Backups;

/// <summary>
/// Replaces all data with a backup: the database first (in one transaction, so a failure changes
/// nothing), then the book files and covers. Runs from the command line, or while the app starts
/// when a restore was requested from the app, so nothing is using the data meanwhile.
/// </summary>
public sealed class RestoreService(
    IServiceScopeFactory scopeFactory,
    PostgresTools tools,
    LibraryStorage storage,
    BackupService backups,
    TimeProvider timeProvider,
    ILogger<RestoreService> logger)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Performs the restore requested from the app, if any. Never throws; the outcome is recorded.</summary>
    public async Task RestorePendingAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(backups.PendingRestoreMarker))
        {
            return;
        }

        BackupService.PendingRestore pending;
        try
        {
            pending = JsonSerializer.Deserialize<BackupService.PendingRestore>(
                await File.ReadAllTextAsync(backups.PendingRestoreMarker, cancellationToken), Json);
        }
        catch (JsonException)
        {
            pending = null;
        }
        finally
        {
            // Removed first, so a restore that crashes the app can't loop on every start.
            File.Delete(backups.PendingRestoreMarker);
        }

        if (pending is not null)
        {
            await RestoreAsync(pending.Path, cancellationToken);
        }
    }

    public async Task<BackupRunResult> RestoreAsync(string archivePath, CancellationToken cancellationToken)
    {
        var started = timeProvider.GetUtcNow();
        var name = Path.GetFileName(archivePath);
        var staging = Path.Combine(storage.Root, $".restore-{Guid.NewGuid():N}");
        BackupRunResult result;
        try
        {
            logger.LogWarning("Restoring backup {Backup}. All current data will be replaced.", name);
            if (!File.Exists(archivePath))
            {
                throw new BackupException($"The backup {name} doesn't exist.");
            }

            var manifest = await BackupArchive.ReadManifestAsync(archivePath, cancellationToken);
            await EnsureCompatibleAsync(scopeFactory, manifest, cancellationToken);
            await WaitForDatabaseAsync(cancellationToken);

            await BackupArchive.ExtractAsync(archivePath, staging, cancellationToken);
            await tools.RestoreAsync(Path.Combine(staging, BackupArchive.DatabaseName), cancellationToken);
            NpgsqlConnection.ClearAllPools();
            ReplaceFolders(staging);

            var message = $"Restored the backup made {manifest.CreatedAt:yyyy-MM-dd HH:mm} UTC " +
                $"({manifest.BookFiles} book file{(manifest.BookFiles == 1 ? "" : "s")}, {manifest.Covers} cover{(manifest.Covers == 1 ? "" : "s")}).";
            result = new BackupRunResult(started, timeProvider.GetUtcNow(), true, message, name);
            logger.LogWarning("{Message}", message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.LogError(ex, "Restoring {Backup} failed.", name);
            var message = ex is BackupException ? ex.Message : $"Restore failed: {ex.Message}";
            result = new BackupRunResult(started, timeProvider.GetUtcNow(), false, message + " Nothing was changed.", name);
        }
        finally
        {
            LibraryStorage.TryDeleteDirectory(staging);
        }

        Directory.CreateDirectory(storage.Root);
        await File.WriteAllTextAsync(backups.RestoreStatusFile, JsonSerializer.Serialize(result, Json), CancellationToken.None);
        return result;
    }

    /// <summary>Backups from newer versions of BookWorm can't be restored: their database has changes this version doesn't know.</summary>
    public static async Task EnsureCompatibleAsync(IServiceScopeFactory scopeFactory, BackupManifest manifest, CancellationToken cancellationToken)
    {
        if (manifest.Migration is null)
        {
            return;
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        if (!db.Database.GetMigrations().Contains(manifest.Migration))
        {
            throw new BackupException(
                $"This backup was made by a newer version of BookWorm ({manifest.Version}). Upgrade BookWorm, then restore it.");
        }
    }

    /// <summary>The database container may still be starting when the app does.</summary>
    private async Task WaitForDatabaseAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        for (var attempt = 1; attempt <= 30; attempt++)
        {
            if (await db.Database.CanConnectAsync(cancellationToken))
            {
                return;
            }

            await Task.Delay(TimeSpan.FromSeconds(2), timeProvider, cancellationToken);
        }

        throw new BackupException("The database isn't reachable.");
    }

    /// <summary>Swaps in the restored books and covers folders; the staging folder is on the same disk, so these are quick renames.</summary>
    private void ReplaceFolders(string staging)
    {
        foreach (var folder in BackupArchive.Folders)
        {
            var current = Path.Combine(storage.Root, folder);
            var restored = Path.Combine(staging, folder);
            if (Directory.Exists(current))
            {
                Directory.Move(current, Path.Combine(staging, folder + ".old"));
            }

            if (Directory.Exists(restored))
            {
                Directory.Move(restored, current);
            }
            else
            {
                Directory.CreateDirectory(current);
            }
        }
    }
}
