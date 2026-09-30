using BookWorm.Server.Auth;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace BookWorm.Server.Data;

internal static class DatabaseInitializer
{
    private const int MaxAttempts = 15;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Brings the database schema up to date and seeds required rows. Runs on every start, so
    /// upgrading BookWorm is just pulling a new image.
    /// </summary>
    public static async Task InitializeDatabaseAsync(this WebApplication app, CancellationToken cancellationToken = default)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("BookWorm.Database");
        var db = services.GetRequiredService<AppDbContext>();

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                // Creating the history table first avoids EF logging a failed query (harmless, but
                // alarming) the very first time BookWorm starts on an empty database.
                await db.GetService<IHistoryRepository>().CreateIfNotExistsAsync(cancellationToken);
                await db.Database.MigrateAsync(cancellationToken);
                break;
            }
            catch (Exception ex) when (attempt < MaxAttempts && IsTransient(ex))
            {
                logger.LogWarning("Database is not reachable yet (attempt {Attempt} of {MaxAttempts}): {Message}", attempt, MaxAttempts, ex.GetBaseException().Message);
                await Task.Delay(RetryDelay, cancellationToken);
            }
        }

        var roleManager = services.GetRequiredService<RoleManager<IdentityRole<Guid>>>();
        if (!await roleManager.RoleExistsAsync(Roles.Admin))
        {
            var result = await roleManager.CreateAsync(new IdentityRole<Guid>(Roles.Admin) { Id = Guid.CreateVersion7() });
            if (!result.Succeeded)
            {
                throw new InvalidOperationException(
                    $"Could not create the {Roles.Admin} role: {string.Join(" ", result.Errors.Select(e => e.Description))}");
            }
        }
    }

    /// <summary>
    /// Connection problems while PostgreSQL is still starting. EF Core wraps these in an
    /// InvalidOperationException, so the whole exception chain is checked.
    /// </summary>
    private static bool IsTransient(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is PostgresException { SqlState: PostgresErrorCodes.CannotConnectNow }
                || current is NpgsqlException { IsTransient: true })
            {
                return true;
            }
        }

        return false;
    }
}
