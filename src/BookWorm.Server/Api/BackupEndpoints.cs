using BookWorm.Contracts;
using BookWorm.Server.Auth;
using BookWorm.Server.Backups;
using BookWorm.Server.Setup;
using BookWorm.Server.Storage;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

namespace BookWorm.Server.Api;

/// <summary>
/// Backups of everyone's data, for administrators; plus restoring on a fresh server during setup.
/// A restore stops the app and runs while it starts again (Docker Compose restarts it).
/// </summary>
internal static class BackupEndpoints
{
    public static void MapBackupEndpoints(this IEndpointRouteBuilder api)
    {
        var admin = api.MapGroup("/admin/backups")
            .WithTags("Admin: backups")
            .RequireAuthorization(Policies.Admin);

        admin.MapGet("/", GetOverview).WithSummary("Backup schedule, status and the backups on the server.");
        admin.MapPut("/settings", UpdateSettings).WithValidation<UpdateBackupSettingsRequest>().WithSummary("Change the backup schedule.");
        admin.MapPost("/", StartBackup).WithSummary("Start a backup now. It runs in the background; poll the overview for the result.");
        admin.MapGet("/{name}", DownloadBackup).WithSummary("Download a backup.");
        admin.MapDelete("/{name}", DeleteBackup).WithSummary("Delete a backup.");
        admin.MapPut("/upload", UploadBackup)
            .WithSummary("Upload a backup (a .tar.gz made by BookWorm) to restore from.");
        admin.MapPost("/{name}/restore", RestoreBackup).WithValidation<RestoreBackupRequest>()
            .WithSummary("Replace ALL data with a backup. BookWorm restarts to do it; everyone is signed out.");
    }

    /// <summary>Available only until the first account exists, for moving an existing library to a new server.</summary>
    public static void MapSetupRestoreEndpoints(this IEndpointRouteBuilder api)
    {
        var setup = api.MapGroup("/setup/backups")
            .WithTags("Setup")
            .AllowAnonymous()
            .AddEndpointFilter(async (context, next) =>
                await context.HttpContext.RequestServices.GetRequiredService<SetupState>().IsSetupRequiredAsync(context.HttpContext.RequestAborted)
                    ? await next(context)
                    : TypedResults.Problem("BookWorm is already set up. Administrators can restore backups from the Backups page.", statusCode: StatusCodes.Status403Forbidden));

        setup.MapPut("/upload", UploadBackup)
            .WithSummary("During first-run setup: upload a backup to restore from.");
        setup.MapPost("/{name}/restore", RestoreBackup).WithValidation<RestoreBackupRequest>()
            .WithSummary("During first-run setup: restore a backup instead of creating a new administrator.");
    }

    private static async Task<Ok<BackupOverview>> GetOverview(BackupService backups, CancellationToken cancellationToken) =>
        TypedResults.Ok(await backups.GetOverviewAsync(cancellationToken));

    private static async Task<Results<Ok<BackupOverview>, ValidationProblem>> UpdateSettings(
        UpdateBackupSettingsRequest request, BackupService backups, CancellationToken cancellationToken)
    {
        if (!TimeZoneInfo.TryFindSystemTimeZoneById(request.TimeZone, out _))
        {
            return ApiErrors.Validation(nameof(UpdateBackupSettingsRequest.TimeZone), "Unknown time zone.");
        }

        await backups.UpdateSettingsAsync(request, cancellationToken);
        return TypedResults.Ok(await backups.GetOverviewAsync(cancellationToken));
    }

    private static async Task<Results<Accepted<BackupOverview>, ProblemHttpResult>> StartBackup(
        BackupService backups, IHostApplicationLifetime lifetime, ILogger<BackupService> logger, CancellationToken cancellationToken)
    {
        if (backups.IsRunning)
        {
            return TypedResults.Problem("A backup is already running.", statusCode: StatusCodes.Status409Conflict);
        }

        if (await backups.CheckToolsAsync(cancellationToken) is { } problem)
        {
            return TypedResults.Problem(problem, statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await backups.RunAsync(lifetime.ApplicationStopping);
            }
            catch (InvalidOperationException)
            {
                // Another backup started in the meantime.
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Backup failed.");
            }
        }, CancellationToken.None);

        // Give the backup a moment to register as running, so the overview shows it.
        for (var i = 0; i < 20 && !backups.IsRunning; i++)
        {
            await Task.Delay(10, cancellationToken);
        }

        return TypedResults.Accepted("/api/admin/backups", await backups.GetOverviewAsync(cancellationToken));
    }

    private static Results<PhysicalFileHttpResult, NotFound> DownloadBackup(string name, BackupService backups) =>
        backups.Find(name) is { } path
            ? TypedResults.PhysicalFile(path, "application/gzip", name, enableRangeProcessing: true)
            : TypedResults.NotFound();

    private static Results<NoContent, NotFound> DeleteBackup(string name, BackupService backups) =>
        backups.Delete(name) ? TypedResults.NoContent() : TypedResults.NotFound();

    private static async Task<Results<Ok<BackupFile>, ValidationProblem, ProblemHttpResult>> UploadBackup(
        HttpContext context, BackupService backups, LibraryStorage storage, CancellationToken cancellationToken)
    {
        using var received = await FileEndpoints.ReceiveAsync(context, storage, long.MaxValue, cancellationToken);
        if (received.Problem is { } problem)
        {
            return problem;
        }

        try
        {
            return TypedResults.Ok(await backups.AddUploadAsync(received.File, cancellationToken));
        }
        catch (BackupException ex)
        {
            return ApiErrors.Validation("file", ex.Message);
        }
    }

    private static async Task<Results<Accepted, NotFound, ValidationProblem>> RestoreBackup(
        string name,
        RestoreBackupRequest request,
        HttpContext context,
        BackupService backups,
        IAppRestarter restarter,
        CancellationToken cancellationToken)
    {
        if (backups.Find(name) is not { } path)
        {
            return TypedResults.NotFound();
        }

        try
        {
            await backups.ScheduleRestoreAsync(path, cancellationToken);
        }
        catch (BackupException ex)
        {
            return ApiErrors.Validation("name", ex.Message);
        }

        restarter.RestartAfter(context.Response);
        return TypedResults.Accepted((string)null);
    }
}
