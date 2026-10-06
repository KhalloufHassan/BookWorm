using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Extensions.Options;
using Npgsql;

namespace BookWorm.Server.Backups;

public sealed class BackupOptions
{
    public const string Section = "Backups";

    /// <summary>Folder the backups are written to. Relative paths are from the app folder.</summary>
    public string Path { get; set; } = "backups";

    /// <summary>
    /// For development and tests: run pg_dump, pg_restore and psql inside this Docker container
    /// (e.g. the development database) instead of expecting them on this machine.
    /// </summary>
    public string PostgresContainer { get; set; }

    /// <summary>Run scheduled backups. Turned off in tests.</summary>
    public bool SchedulerEnabled { get; set; } = true;
}

/// <summary>
/// Runs PostgreSQL's own tools to dump and restore the database. The Docker image includes them;
/// on a development machine they can run inside the database container instead.
/// </summary>
public sealed class PostgresTools(IConfiguration configuration, IOptions<BackupOptions> options, ILogger<PostgresTools> logger)
{
    private string Container => string.IsNullOrWhiteSpace(options.Value.PostgresContainer) ? null : options.Value.PostgresContainer;

    private NpgsqlConnectionStringBuilder Connection => new(configuration.GetConnectionString("Database"));

    /// <summary>Null when the tools work; otherwise what's wrong, for the backups page.</summary>
    public async Task<string> CheckAsync(CancellationToken cancellationToken)
    {
        try
        {
            var result = Container is null
                ? await RunAsync("pg_dump", ["--version"], null, cancellationToken)
                : await RunAsync("docker", ["exec", Container, "pg_dump", "--version"], null, cancellationToken);
            return result.ExitCode == 0 ? null : $"pg_dump doesn't work: {result.Error}";
        }
        catch (Win32Exception)
        {
            return Container is null
                ? "PostgreSQL's client tools (pg_dump, pg_restore and psql, version 18) aren't installed on this server. They come with the BookWorm Docker image."
                : "The docker command isn't available, so the database container's tools can't be used.";
        }
    }

    /// <summary>Writes the whole database to <paramref name="outputFile"/> in pg_dump's compressed custom format.</summary>
    public async Task DumpAsync(string outputFile, CancellationToken cancellationToken)
    {
        if (Container is null)
        {
            await RunCheckedAsync("pg_dump", ["--format=custom", "--no-owner", "--no-privileges", $"--file={outputFile}"], LocalEnvironment(), cancellationToken);
            return;
        }

        var remote = $"/tmp/bookworm-{Guid.NewGuid():N}.dump";
        try
        {
            await RunCheckedAsync("docker", ["exec", Container, "pg_dump", .. ContainerLogin(), "--format=custom", "--no-owner", "--no-privileges", $"--file={remote}"], null, cancellationToken);
            await RunCheckedAsync("docker", ["cp", $"{Container}:{remote}", outputFile], null, cancellationToken);
        }
        finally
        {
            await RunAsync("docker", ["exec", Container, "rm", "-f", remote], null, CancellationToken.None);
        }
    }

    /// <summary>
    /// Replaces everything in the database with the dump, in a single transaction: if anything
    /// fails, the database is left exactly as it was.
    /// </summary>
    public async Task RestoreAsync(string dumpFile, CancellationToken cancellationToken)
    {
        const string clear = "DROP SCHEMA public CASCADE; CREATE SCHEMA public;";
        string[] psqlOptions = ["--no-psqlrc", "--quiet", "--set=ON_ERROR_STOP=1", "--single-transaction", $"--command={clear}"];

        if (Container is null)
        {
            var sql = Path.ChangeExtension(dumpFile, ".sql");
            try
            {
                await RunCheckedAsync("pg_restore", ["--no-owner", "--no-privileges", $"--file={sql}", dumpFile], LocalEnvironment(), cancellationToken);
                await RunCheckedAsync("psql", [.. psqlOptions, $"--file={sql}"], LocalEnvironment(), cancellationToken);
            }
            finally
            {
                File.Delete(sql);
            }

            return;
        }

        var remote = $"/tmp/bookworm-{Guid.NewGuid():N}";
        try
        {
            await RunCheckedAsync("docker", ["cp", dumpFile, $"{Container}:{remote}.dump"], null, cancellationToken);
            await RunCheckedAsync("docker", ["exec", Container, "pg_restore", "--no-owner", "--no-privileges", $"--file={remote}.sql", $"{remote}.dump"], null, cancellationToken);
            await RunCheckedAsync("docker", ["exec", Container, "psql", .. ContainerLogin(), .. psqlOptions, $"--file={remote}.sql"], null, cancellationToken);
        }
        finally
        {
            await RunAsync("docker", ["exec", Container, "rm", "-f", $"{remote}.dump", $"{remote}.sql"], null, CancellationToken.None);
        }
    }

    /// <summary>Inside the database container, the local socket accepts the database user without a password.</summary>
    private string[] ContainerLogin() => [$"--username={Connection.Username}", $"--dbname={Connection.Database}"];

    private Dictionary<string, string> LocalEnvironment()
    {
        var connection = Connection;
        return new Dictionary<string, string>
        {
            ["PGHOST"] = connection.Host ?? "localhost",
            ["PGPORT"] = connection.Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["PGDATABASE"] = connection.Database ?? "",
            ["PGUSER"] = connection.Username ?? "",
            ["PGPASSWORD"] = connection.Password ?? "",
            ["PGSSLMODE"] = connection.SslMode switch
            {
                SslMode.Disable => "disable",
                SslMode.Allow => "allow",
                SslMode.Require => "require",
                SslMode.VerifyCA => "verify-ca",
                SslMode.VerifyFull => "verify-full",
                _ => "prefer",
            },
            ["PGGSSENCMODE"] = "disable",
            ["PGAPPNAME"] = "bookworm-backup",
            ["PGCONNECT_TIMEOUT"] = "30",
        };
    }

    private async Task RunCheckedAsync(string tool, string[] arguments, Dictionary<string, string> environment, CancellationToken cancellationToken)
    {
        var result = await RunAsync(tool, arguments, environment, cancellationToken);
        if (result.ExitCode != 0)
        {
            var command = tool == "docker" && arguments.Length > 2 ? arguments[2] : tool;
            throw new BackupException($"{command} failed: {result.Error}");
        }
    }

    private async Task<(int ExitCode, string Error)> RunAsync(
        string tool, string[] arguments, Dictionary<string, string> environment, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(tool)
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        foreach (var (key, value) in environment ?? [])
        {
            start.Environment[key] = value;
        }

        using var process = Process.Start(start) ?? throw new BackupException($"Could not start {tool}.");
        var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var error = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw;
        }

        await output;
        var message = Tail((await error).Trim(), 1500);
        if (process.ExitCode != 0)
        {
            logger.LogWarning("{Tool} exited with code {ExitCode}: {Error}", tool, process.ExitCode, message);
        }

        return (process.ExitCode, message.Length > 0 ? message : $"exit code {process.ExitCode}");
    }

    private static string Tail(string text, int length) => text.Length <= length ? text : "…" + text[^length..];
}

/// <summary>A backup or restore failed for a reason worth showing to the administrator.</summary>
public sealed class BackupException(string message, Exception inner = null) : Exception(message, inner);
