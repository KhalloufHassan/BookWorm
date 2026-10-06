using System.Text.Json;
using System.Text.RegularExpressions;
using BookWorm.Contracts;
using BookWorm.Server.Api;
using BookWorm.Server.Data;
using BookWorm.Server.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace BookWorm.Server.Backups;

/// <summary>
/// Makes, lists and deletes backups, keeps the schedule, and hands restores over to
/// <see cref="RestoreService"/> (restores run while the app starts, before anything uses the data).
/// </summary>
public sealed partial class BackupService(
    IServiceScopeFactory scopeFactory,
    PostgresTools tools,
    LibraryStorage storage,
    IOptions<BackupOptions> options,
    IHostEnvironment environment,
    TimeProvider timeProvider,
    ILogger<BackupService> logger)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan ToolsCheckInterval = TimeSpan.FromMinutes(1);

    private readonly SemaphoreSlim _runLock = new(1, 1);
    private CancellationTokenSource _settingsChanged = new();
    private (DateTimeOffset CheckedAt, string Problem)? _toolsCheck;

    public string Folder { get; } = Path.GetFullPath(options.Value.Path, environment.ContentRootPath);

    public bool IsRunning { get; private set; }

    /// <summary>Cancelled whenever the schedule changes, so the scheduler recalculates the next run.</summary>
    public CancellationToken SettingsChanged => _settingsChanged.Token;

    public string PendingRestoreMarker => Path.Combine(storage.Root, "restore-pending.json");

    public string RestoreStatusFile => Path.Combine(storage.Root, "restore-status.json");

    [GeneratedRegex(@"^bookworm-(backup|upload)-\d{8}-\d{6}(-\d+)?\.tar\.gz$")]
    private static partial Regex BackupName();

    public async Task<BackupSettingsRow> GetSettingsAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.BackupSettings.AsNoTracking().SingleOrDefaultAsync(cancellationToken) ?? new BackupSettingsRow();
    }

    public async Task<BackupSettingsRow> UpdateSettingsAsync(UpdateBackupSettingsRequest request, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var settings = await db.BackupSettings.SingleOrDefaultAsync(cancellationToken);
        if (settings is null)
        {
            settings = new BackupSettingsRow();
            db.BackupSettings.Add(settings);
        }

        settings.Enabled = request.Enabled;
        settings.Frequency = request.Frequency;
        settings.DayOfWeek = request.DayOfWeek;
        settings.TimeOfDay = new TimeOnly(request.TimeOfDay.Hour, request.TimeOfDay.Minute);
        settings.TimeZone = request.TimeZone;
        settings.KeepCount = request.KeepCount;
        await db.SaveChangesAsync(cancellationToken);

        var previous = Interlocked.Exchange(ref _settingsChanged, new CancellationTokenSource());
        await previous.CancelAsync();
        previous.Dispose();
        return settings;
    }

    public async Task<BackupOverview> GetOverviewAsync(CancellationToken cancellationToken)
    {
        var settings = await GetSettingsAsync(cancellationToken);
        BackupRunResult lastRun = settings.LastRunStartedAt is { } started
            ? new BackupRunResult(started, settings.LastRunFinishedAt, settings.LastRunSucceeded == true, settings.LastRunMessage, settings.LastRunFile)
            : null;

        return new BackupOverview(
            ToContract(settings),
            IsRunning,
            BackupSchedule.NextRun(settings, timeProvider.GetUtcNow()),
            lastRun,
            await ReadRestoreStatusAsync(cancellationToken),
            List(),
            await CheckToolsAsync(cancellationToken),
            Folder);
    }

    public async Task<string> CheckToolsAsync(CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        if (_toolsCheck is { } check && now - check.CheckedAt < ToolsCheckInterval)
        {
            return check.Problem;
        }

        var problem = await tools.CheckAsync(cancellationToken);
        _toolsCheck = (now, problem);
        return problem;
    }

    public IReadOnlyList<BackupFile> List()
    {
        if (!Directory.Exists(Folder))
        {
            return [];
        }

        return new DirectoryInfo(Folder).EnumerateFiles("bookworm-*.tar.gz")
            .Where(file => BackupName().IsMatch(file.Name))
            .Select(file => new BackupFile(
                file.Name,
                file.Name.StartsWith("bookworm-upload-", StringComparison.Ordinal) ? BackupKind.Uploaded : BackupKind.Created,
                new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero),
                file.Length))
            .OrderByDescending(file => file.CreatedAt)
            .ToList();
    }

    /// <summary>The full path of a backup in the backups folder, or null for unknown or malformed names.</summary>
    public string Find(string name)
    {
        if (!BackupName().IsMatch(name))
        {
            return null;
        }

        var path = Path.Combine(Folder, name);
        return File.Exists(path) ? path : null;
    }

    public bool Delete(string name)
    {
        var path = Find(name);
        if (path is null)
        {
            return false;
        }

        File.Delete(path);
        return true;
    }

    /// <summary>Makes a backup now. Throws <see cref="InvalidOperationException"/> when one is already running.</summary>
    public async Task<BackupRunResult> RunAsync(CancellationToken cancellationToken)
    {
        if (!await _runLock.WaitAsync(0, cancellationToken))
        {
            throw new InvalidOperationException("A backup is already running.");
        }

        IsRunning = true;
        var started = timeProvider.GetUtcNow();
        Directory.CreateDirectory(Folder);
        Directory.CreateDirectory(storage.TempRoot);
        var name = UniqueName("backup", started);
        var partial = Path.Combine(Folder, $".{name}.partial");
        var dump = Path.Combine(storage.TempRoot, $"backup-{Guid.NewGuid():N}.dump");
        BackupRunResult result;
        try
        {
            await RecordRunAsync(new BackupRunResult(started, null, false, "Running…", null), cancellationToken);

            await tools.DumpAsync(dump, cancellationToken);
            var manifest = new BackupManifest(
                BackupArchive.CurrentFormat,
                "BookWorm",
                ServerVersion.Current,
                started,
                await LastMigrationAsync(cancellationToken),
                CountFiles(storage.BooksRoot),
                CountFiles(storage.CoversRoot));
            await BackupArchive.WriteAsync(partial, manifest, dump, storage.Root, cancellationToken);
            File.Move(partial, Path.Combine(Folder, name));

            var removed = await ApplyRetentionAsync(cancellationToken);
            var size = FileSizes.Describe(new FileInfo(Path.Combine(Folder, name)).Length);
            var message = removed > 0 ? $"Saved {size}. Removed {removed} older backup{(removed == 1 ? "" : "s")}." : $"Saved {size}.";
            result = new BackupRunResult(started, timeProvider.GetUtcNow(), true, message, name);
            logger.LogInformation("Backup {Name} finished: {Message}", name, message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.LogError(ex, "Backup failed.");
            result = new BackupRunResult(started, timeProvider.GetUtcNow(), false, ex is BackupException ? ex.Message : $"Backup failed: {ex.Message}", null);
        }
        finally
        {
            LibraryStorage.TryDeleteFile(partial);
            LibraryStorage.TryDeleteFile(dump);
            IsRunning = false;
            _runLock.Release();
        }

        await RecordRunAsync(result, CancellationToken.None);
        return result;
    }

    /// <summary>Saves an uploaded backup into the backups folder after checking that it is one.</summary>
    public async Task<BackupFile> AddUploadAsync(ReceivedFile upload, CancellationToken cancellationToken)
    {
        await BackupArchive.ReadManifestAsync(upload.Path, cancellationToken);
        var name = UniqueName("upload", timeProvider.GetUtcNow());
        upload.MoveTo(Path.Combine(Folder, name));
        return List().Single(file => file.Name == name);
    }

    /// <summary>
    /// Checks the backup and asks for it to be restored the next time the app starts. The caller
    /// then stops the app; under Docker Compose it starts again by itself.
    /// </summary>
    public async Task ScheduleRestoreAsync(string path, CancellationToken cancellationToken)
    {
        var manifest = await BackupArchive.ReadManifestAsync(path, cancellationToken);
        await RestoreService.EnsureCompatibleAsync(scopeFactory, manifest, cancellationToken);
        await File.WriteAllTextAsync(PendingRestoreMarker, JsonSerializer.Serialize(new PendingRestore(path, timeProvider.GetUtcNow()), Json), cancellationToken);
        logger.LogWarning("Restore of {Backup} requested; it runs when BookWorm restarts.", Path.GetFileName(path));
    }

    public async Task<BackupRunResult> ReadRestoreStatusAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(RestoreStatusFile))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<BackupRunResult>(await File.ReadAllTextAsync(RestoreStatusFile, cancellationToken), Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static BackupSettings ToContract(BackupSettingsRow row) =>
        new(row.Enabled, row.Frequency, row.DayOfWeek, row.TimeOfDay, row.TimeZone, row.KeepCount);

    private async Task<int> ApplyRetentionAsync(CancellationToken cancellationToken)
    {
        var keep = (await GetSettingsAsync(cancellationToken)).KeepCount;
        var old = List().Where(file => file.Kind == BackupKind.Created).Skip(Math.Max(1, keep)).ToList();
        foreach (var file in old)
        {
            File.Delete(Path.Combine(Folder, file.Name));
        }

        return old.Count;
    }

    private async Task RecordRunAsync(BackupRunResult result, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var settings = await db.BackupSettings.SingleOrDefaultAsync(cancellationToken);
        if (settings is null)
        {
            settings = new BackupSettingsRow();
            db.BackupSettings.Add(settings);
        }

        settings.LastRunStartedAt = result.StartedAt;
        settings.LastRunFinishedAt = result.FinishedAt;
        settings.LastRunSucceeded = result.FinishedAt is null ? null : result.Succeeded;
        settings.LastRunMessage = result.Message?.Length > 2000 ? result.Message[..2000] : result.Message;
        settings.LastRunFile = result.FileName;
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task<string> LastMigrationAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        // Only migrations this version has: a database from before the migrations were squashed
        // still lists the old ones, which a restore would take for a newer version's.
        var known = db.Database.GetMigrations().ToHashSet();
        return (await db.Database.GetAppliedMigrationsAsync(cancellationToken)).LastOrDefault(known.Contains);
    }

    private string UniqueName(string kind, DateTimeOffset time)
    {
        var name = $"bookworm-{kind}-{time.UtcDateTime:yyyyMMdd-HHmmss}.tar.gz";
        for (var n = 2; File.Exists(Path.Combine(Folder, name)); n++)
        {
            name = $"bookworm-{kind}-{time.UtcDateTime:yyyyMMdd-HHmmss}-{n}.tar.gz";
        }

        return name;
    }

    private static long CountFiles(string folder) =>
        Directory.Exists(folder) ? Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).LongCount() : 0;

    public sealed record PendingRestore(string Path, DateTimeOffset RequestedAt);
}

/// <summary>When the next scheduled backup is due.</summary>
public static class BackupSchedule
{
    public static DateTimeOffset? NextRun(BackupSettingsRow settings, DateTimeOffset now)
    {
        if (!settings.Enabled)
        {
            return null;
        }

        var zone = TimeZoneInfo.TryFindSystemTimeZoneById(settings.TimeZone, out var found) ? found : TimeZoneInfo.Utc;
        var localNow = TimeZoneInfo.ConvertTime(now, zone).DateTime;

        for (var days = 0; days <= 8; days++)
        {
            var date = localNow.Date.AddDays(days);
            if (settings.Frequency == BackupFrequency.Weekly && date.DayOfWeek != settings.DayOfWeek)
            {
                continue;
            }

            var local = DateTime.SpecifyKind(date + settings.TimeOfDay.ToTimeSpan(), DateTimeKind.Unspecified);
            if (zone.IsInvalidTime(local))
            {
                // Skipped by a daylight saving change: run an hour later instead.
                local = local.AddHours(1);
            }

            var utc = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local, zone), TimeSpan.Zero);
            if (utc > now)
            {
                return utc;
            }
        }

        return null;
    }
}

/// <summary>Runs the scheduled backups.</summary>
public sealed class BackupScheduler(
    BackupService backups,
    IOptions<BackupOptions> options,
    TimeProvider timeProvider,
    ILogger<BackupScheduler> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.SchedulerEnabled)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            DateTimeOffset? next;
            try
            {
                next = BackupSchedule.NextRun(await backups.GetSettingsAsync(stoppingToken), timeProvider.GetUtcNow());
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogError(ex, "Could not read the backup schedule; trying again in a minute.");
                await Task.Delay(TimeSpan.FromMinutes(1), timeProvider, stoppingToken);
                continue;
            }

            using var wake = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, backups.SettingsChanged);
            try
            {
                var delay = next is { } due
                    ? TimeSpan.FromTicks(Math.Max(0, (due - timeProvider.GetUtcNow()).Ticks))
                    : Timeout.InfiniteTimeSpan; // Backups are off: sleep until the schedule changes.
                await Task.Delay(delay, timeProvider, wake.Token);
            }
            catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
            {
                continue; // The schedule changed.
            }

            try
            {
                await backups.RunAsync(stoppingToken);
            }
            catch (InvalidOperationException)
            {
                // A manual backup is running right now; that one counts.
            }
        }
    }
}
