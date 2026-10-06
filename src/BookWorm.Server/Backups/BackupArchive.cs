using System.Formats.Tar;
using System.IO.Compression;
using System.Text.Json;

namespace BookWorm.Server.Backups;

/// <summary>Describes a backup; the first entry of every backup archive.</summary>
public sealed record BackupManifest(
    int Format,
    string App,
    string Version,
    DateTimeOffset CreatedAt,
    string Migration,
    long BookFiles,
    long Covers);

/// <summary>
/// A backup is one <c>.tar.gz</c> file:
/// <code>
/// bookworm-backup.json   the manifest
/// database.dump          pg_dump custom format
/// books/…                book files, as in the data folder
/// covers/…               cover images
/// </code>
/// Book files are mostly compressed already, so the archive uses fast compression.
/// </summary>
public static class BackupArchive
{
    public const int CurrentFormat = 1;
    public const string ManifestName = "bookworm-backup.json";
    public const string DatabaseName = "database.dump";
    public static readonly string[] Folders = ["books", "covers"];

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static async Task WriteAsync(
        string archivePath, BackupManifest manifest, string databaseDump, string dataRoot, CancellationToken cancellationToken)
    {
        await using var file = new FileStream(archivePath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.Asynchronous);
        await using var gzip = new GZipStream(file, CompressionLevel.Fastest);
        await using var tar = new TarWriter(gzip, TarEntryFormat.Pax, leaveOpen: false);

        var manifestEntry = new PaxTarEntry(TarEntryType.RegularFile, ManifestName)
        {
            DataStream = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(manifest, Json)),
            ModificationTime = manifest.CreatedAt,
        };
        await tar.WriteEntryAsync(manifestEntry, cancellationToken);
        await tar.WriteEntryAsync(databaseDump, DatabaseName, cancellationToken);

        foreach (var folder in Folders)
        {
            var root = Path.Combine(dataRoot, folder);
            if (!Directory.Exists(root))
            {
                continue;
            }

            foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                var name = $"{folder}/{Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/')}";
                await tar.WriteEntryAsync(path, name, cancellationToken);
            }
        }
    }

    /// <summary>Reads the manifest, which must be the first entry; throws <see cref="BackupException"/> if the file isn't a BookWorm backup.</summary>
    public static async Task<BackupManifest> ReadManifestAsync(string archivePath, CancellationToken cancellationToken)
    {
        try
        {
            await using var file = File.OpenRead(archivePath);
            await using var gzip = new GZipStream(file, CompressionMode.Decompress);
            await using var tar = new TarReader(gzip);
            var entry = await tar.GetNextEntryAsync(copyData: false, cancellationToken);
            if (entry is not { Name: ManifestName, DataStream: not null })
            {
                throw new BackupException("This isn't a BookWorm backup.");
            }

            var manifest = await JsonSerializer.DeserializeAsync<BackupManifest>(entry.DataStream, Json, cancellationToken)
                ?? throw new BackupException("The backup's description is empty.");
            if (manifest.Format > CurrentFormat)
            {
                throw new BackupException("This backup was made by a newer version of BookWorm. Upgrade BookWorm first.");
            }

            return manifest;
        }
        catch (Exception ex) when (ex is InvalidDataException or JsonException or EndOfStreamException or FormatException)
        {
            throw new BackupException("This isn't a BookWorm backup, or the file is damaged.", ex);
        }
    }

    /// <summary>
    /// Unpacks the database dump and the files into <paramref name="destination"/>. Only regular files
    /// inside the expected folders are extracted; anything else (links, paths leaving the folder) is refused.
    /// </summary>
    public static async Task ExtractAsync(string archivePath, string destination, CancellationToken cancellationToken)
    {
        var root = Path.GetFullPath(destination) + Path.DirectorySeparatorChar;
        try
        {
            await using var file = File.OpenRead(archivePath);
            await using var gzip = new GZipStream(file, CompressionMode.Decompress);
            await using var tar = new TarReader(gzip);
            while (await tar.GetNextEntryAsync(copyData: false, cancellationToken) is { } entry)
            {
                if (entry.EntryType is TarEntryType.Directory || entry.Name == ManifestName)
                {
                    continue;
                }

                var allowed = entry.Name == DatabaseName || Folders.Any(folder => entry.Name.StartsWith(folder + "/", StringComparison.Ordinal));
                if (!allowed || entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile))
                {
                    throw new BackupException($"The backup contains an unexpected entry: {entry.Name}");
                }

                var target = Path.GetFullPath(Path.Combine(root, entry.Name));
                if (!target.StartsWith(root, StringComparison.Ordinal))
                {
                    throw new BackupException($"The backup contains an unsafe path: {entry.Name}");
                }

                Directory.CreateDirectory(Path.GetDirectoryName(target));
                await entry.ExtractToFileAsync(target, overwrite: false, cancellationToken);
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException or FormatException)
        {
            throw new BackupException("The backup file is damaged.", ex);
        }

        if (!File.Exists(Path.Combine(root, DatabaseName)))
        {
            throw new BackupException("The backup has no database in it.");
        }
    }
}
