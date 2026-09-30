using System.Net;
using System.Net.Http.Json;
using BookWorm.Contracts;
using BookWorm.Server.Backups;
using BookWorm.Server.Data;
using BookWorm.Server.Tests.Infrastructure;
using BookWorm.UI.Api;
using Microsoft.Extensions.DependencyInjection;

namespace BookWorm.Server.Tests;

/// <summary>Backups against the shared test server. Nothing here actually restores; see <see cref="BackupRestoreTests"/>.</summary>
public sealed class BackupApiTests(BookWormAppFactory app)
{
    [Fact]
    public async Task Backups_CanBeMadeDownloadedAndDeleted_AndRestoresWaitForARestart()
    {
        var admin = await app.CreateUserAsync(isAdmin: true);
        var book = await admin.Api.AddBookAsync("Backed up");
        await admin.Api.UploadSampleAsync(book.Id, BookFormat.Epub);

        await admin.Api.StartBackupAsync();
        var overview = await WaitForBackupAsync(admin.Api);

        Assert.True(overview.LastRun?.Succeeded, overview.LastRun?.Message);
        var backup = Assert.Single(overview.Backups, b => b.Name == overview.LastRun.FileName);
        Assert.Equal(BackupKind.Created, backup.Kind);
        Assert.Null(overview.ToolsProblem);

        using var download = await admin.Http.GetAsync(BookWormApiClient.BackupUrl(backup.Name));
        download.EnsureSuccessStatusCode();
        var archive = Path.GetTempFileName();
        try
        {
            await File.WriteAllBytesAsync(archive, await download.Content.ReadAsByteArrayAsync());
            var manifest = await BackupArchive.ReadManifestAsync(archive, CancellationToken.None);
            Assert.Equal("BookWorm", manifest.App);
            Assert.NotNull(manifest.Migration);
            Assert.True(manifest.BookFiles >= 1);
        }
        finally
        {
            File.Delete(archive);
        }

        var restartsBefore = app.Restarter.Requests;
        await admin.Api.RestoreBackupAsync(backup.Name);
        var backups = app.Services.GetRequiredService<BackupService>();
        Assert.True(File.Exists(backups.PendingRestoreMarker));
        Assert.Equal(restartsBefore + 1, app.Restarter.Requests);
        File.Delete(backups.PendingRestoreMarker);

        await admin.Api.DeleteBackupAsync(backup.Name);
        Assert.DoesNotContain((await admin.Api.GetBackupsAsync()).Backups, b => b.Name == backup.Name);
    }

    [Fact]
    public async Task TheSchedule_CanBeChanged()
    {
        var admin = (await app.CreateUserAsync(isAdmin: true)).Api;

        var overview = await admin.UpdateBackupSettingsAsync(new UpdateBackupSettingsRequest
        {
            Enabled = true,
            Frequency = BackupFrequency.Weekly,
            DayOfWeek = DayOfWeek.Saturday,
            TimeOfDay = new TimeOnly(2, 30),
            TimeZone = "Europe/Berlin",
            KeepCount = 4,
        });

        Assert.Equal(new BackupSettings(true, BackupFrequency.Weekly, DayOfWeek.Saturday, new TimeOnly(2, 30), "Europe/Berlin", 4), overview.Settings);
        Assert.NotNull(overview.NextRunAt);
        var local = TimeZoneInfo.ConvertTime(overview.NextRunAt.Value, TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin"));
        Assert.Equal(DayOfWeek.Saturday, local.DayOfWeek);
        Assert.Equal(new TimeOnly(2, 30), TimeOnly.FromDateTime(local.DateTime));

        var error = await Assert.ThrowsAsync<ApiException>(() => admin.UpdateBackupSettingsAsync(new UpdateBackupSettingsRequest { TimeZone = "Nowhere/Special" }));
        Assert.Equal(HttpStatusCode.BadRequest, error.StatusCode);
    }

    [Fact]
    public async Task Backups_AreForAdministratorsOnly()
    {
        var reader = await app.CreateUserAsync();

        var list = await Assert.ThrowsAsync<ApiException>(() => reader.Api.GetBackupsAsync());
        var start = await Assert.ThrowsAsync<ApiException>(() => reader.Api.StartBackupAsync());

        Assert.Equal(HttpStatusCode.Forbidden, list.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, start.StatusCode);
    }

    [Theory]
    [InlineData("..%2F..%2Fetc%2Fpasswd")]
    [InlineData("bookworm-backup-20260101-000000.tar.gz")]
    [InlineData("database.dump")]
    public async Task UnknownBackupNames_AreNotFound(string name)
    {
        var admin = await app.CreateUserAsync(isAdmin: true);

        using var response = await admin.Http.GetAsync($"api/admin/backups/{name}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Uploads_MustBeBookWormBackups()
    {
        var admin = await app.CreateUserAsync(isAdmin: true);

        using var response = await admin.Http.PutAsync(BookWormApiClient.BackupUploadUrl, new ByteArrayContent(SampleFiles.Epub("nope")));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task BackupsFromNewerVersions_AreRefused()
    {
        var admin = await app.CreateUserAsync(isAdmin: true);
        var folder = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var dump = Path.Combine(folder, "database.dump");
            await File.WriteAllTextAsync(dump, "not really a dump");
            var archive = Path.Combine(folder, "future.tar.gz");
            await BackupArchive.WriteAsync(archive, new BackupManifest(1, "BookWorm", "99.0.0", DateTimeOffset.UtcNow, "29991231000000_FromTheFuture", 0, 0), dump, folder, CancellationToken.None);

            using var upload = await admin.Http.PutAsync(BookWormApiClient.BackupUploadUrl, new ByteArrayContent(await File.ReadAllBytesAsync(archive)));
            upload.EnsureSuccessStatusCode();
            var uploaded = (await upload.Content.ReadFromJsonAsync<BackupFile>(BookWormJson.Options));
            Assert.Equal(BackupKind.Uploaded, uploaded.Kind);

            var error = await Assert.ThrowsAsync<ApiException>(() => admin.Api.RestoreBackupAsync(uploaded.Name));

            Assert.Equal(HttpStatusCode.BadRequest, error.StatusCode);
            Assert.Contains("newer version", Assert.Single(error.Errors["name"]));
            await admin.Api.DeleteBackupAsync(uploaded.Name);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task SetupRestore_IsClosedOnceAccountsExist()
    {
        using var anonymous = app.CreateClient();

        using var upload = await anonymous.PutAsync("api/setup/backups/upload", new ByteArrayContent([1, 2, 3]));
        using var restore = await anonymous.PostAsJsonAsync("api/setup/backups/whatever.tar.gz/restore", new RestoreBackupRequest { Confirm = true });

        Assert.Equal(HttpStatusCode.Forbidden, upload.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, restore.StatusCode);
    }

    [Fact]
    public void TheNextRun_FollowsTheSchedule()
    {
        var settings = new BackupSettingsRow { Frequency = BackupFrequency.Daily, TimeOfDay = new TimeOnly(3, 0), TimeZone = "UTC" };

        Assert.Equal(new DateTimeOffset(2026, 9, 30, 3, 0, 0, TimeSpan.Zero),
            BackupSchedule.NextRun(settings, new DateTimeOffset(2026, 9, 29, 3, 0, 0, TimeSpan.Zero)));
        Assert.Equal(new DateTimeOffset(2026, 9, 29, 3, 0, 0, TimeSpan.Zero),
            BackupSchedule.NextRun(settings, new DateTimeOffset(2026, 9, 29, 2, 59, 0, TimeSpan.Zero)));

        settings.Frequency = BackupFrequency.Weekly;
        settings.DayOfWeek = DayOfWeek.Monday; // 2026-09-29 is a Tuesday.
        Assert.Equal(new DateTimeOffset(2026, 10, 5, 3, 0, 0, TimeSpan.Zero),
            BackupSchedule.NextRun(settings, new DateTimeOffset(2026, 9, 29, 12, 0, 0, TimeSpan.Zero)));

        settings.Enabled = false;
        Assert.Null(BackupSchedule.NextRun(settings, DateTimeOffset.UtcNow));
    }

    internal static async Task<BackupOverview> WaitForBackupAsync(BookWormApiClient api)
    {
        for (var i = 0; i < 300; i++)
        {
            var overview = await api.GetBackupsAsync();
            if (!overview.IsRunning && overview.LastRun?.FinishedAt is not null)
            {
                return overview;
            }

            await Task.Delay(200);
        }

        throw new TimeoutException("The backup didn't finish in time.");
    }
}

/// <summary>Real restores, on a server of their own (they replace the whole database).</summary>
public sealed class BackupRestoreTests : IAsyncLifetime
{
    private readonly BookWormAppFactory _app = new();

    public ValueTask InitializeAsync() => _app.InitializeAsync();

    public ValueTask DisposeAsync() => _app.DisposeAsync();

    [Fact]
    public async Task ARestore_BringsBackTheDatabaseAndTheFiles()
    {
        var user = await _app.CreateUserAsync();
        var book = await user.Api.AddBookAsync("Kept safe", notes: "Before the backup.");
        var file = await user.Api.UploadSampleAsync(book.Id, BookFormat.Epub, "Original text.");
        await user.Api.UploadCoverAsync(book.Id, new MemoryStream(SampleFiles.Png), "image/png");

        var backups = _app.Services.GetRequiredService<BackupService>();
        var backup = await backups.RunAsync(CancellationToken.None);
        Assert.True(backup.Succeeded, backup.Message);

        // Changes after the backup, which the restore undoes.
        await user.Api.AddBookAsync("Added later");
        await user.Api.DeleteFileAsync(book.Id, file.Id);
        await user.Api.DeleteCoverAsync(book.Id);

        var restore = await _app.Services.GetRequiredService<RestoreService>()
            .RestoreAsync(Path.Combine(backups.Folder, backup.FileName), CancellationToken.None);

        Assert.True(restore.Succeeded, restore.Message);
        var books = await user.Api.GetBooksAsync();
        var restored = Assert.Single(books.Items);
        Assert.Equal("Kept safe", restored.Title);
        Assert.NotNull(restored.CoverVersion);
        var details = await user.Api.GetBookAsync(book.Id);
        Assert.Equal(file.Id, Assert.Single(details.Files).Id);
        using var content = await user.Http.GetAsync(BookWormApiClient.FileUrl(book.Id, file.Id));
        Assert.Equal(SampleFiles.Epub("Original text."), await content.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task ARestoreRequestedInTheApp_RunsWhenTheAppStarts()
    {
        var admin = await _app.CreateUserAsync(isAdmin: true);
        await admin.Api.AddBookAsync("In the backup");
        var backups = _app.Services.GetRequiredService<BackupService>();
        var backup = await backups.RunAsync(CancellationToken.None);
        await admin.Api.AddBookAsync("Not in the backup");

        await admin.Api.RestoreBackupAsync(backup.FileName);
        await _app.Services.GetRequiredService<RestoreService>().RestorePendingAsync(CancellationToken.None);

        Assert.False(File.Exists(backups.PendingRestoreMarker));
        Assert.Equal(["In the backup"], (await admin.Api.GetBooksAsync()).Items.Select(b => b.Title));
        var overview = await admin.Api.GetBackupsAsync();
        Assert.True(overview.LastRestore?.Succeeded, overview.LastRestore?.Message);
        Assert.Equal(backup.FileName, overview.LastRestore.FileName);
    }
}
