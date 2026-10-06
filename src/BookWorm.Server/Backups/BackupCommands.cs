using BookWorm.Server.Data;

namespace BookWorm.Server.Backups;

/// <summary>
/// Command-line backup and restore, for scripts and for moving to a new server:
/// <code>
/// docker compose run --rm app backup
/// docker compose stop app
/// docker compose run --rm app restore bookworm-backup-20260929-030000.tar.gz
/// docker compose start app
/// </code>
/// </summary>
internal static class BackupCommands
{
    private const string Usage = """
        Usage:
          backup                make a backup now
          restore <file>        replace ALL data with a backup (stop the app first)
        A file name without a folder is looked up in the backups folder.
        """;

    /// <summary>Separates a command from the arguments meant for the web host.</summary>
    public static (string[] Command, string[] HostArguments) Split(string[] args) =>
        args is ["backup", ..] or ["restore", ..] ? (args, []) : ([], args);

    public static async Task<int> RunAsync(WebApplication app, string[] command)
    {
        var backups = app.Services.GetRequiredService<BackupService>();
        switch (command)
        {
            case ["backup"]:
            {
                await app.InitializeDatabaseAsync();
                var result = await backups.RunAsync(CancellationToken.None);
                Console.WriteLine(result.Succeeded ? $"Backup {result.FileName}: {result.Message}" : result.Message);
                return result.Succeeded ? 0 : 1;
            }

            case ["restore", var file]:
            {
                var path = file.Contains('/') || file.Contains(Path.DirectorySeparatorChar)
                    ? Path.GetFullPath(file)
                    : Path.Combine(backups.Folder, file);
                var result = await app.Services.GetRequiredService<RestoreService>().RestoreAsync(path, CancellationToken.None);
                Console.WriteLine(result.Message);
                if (!result.Succeeded)
                {
                    return 1;
                }

                // Bring a backup from an older version up to date right away.
                await app.InitializeDatabaseAsync();
                return 0;
            }

            default:
                Console.Error.WriteLine(Usage);
                return 2;
        }
    }
}
