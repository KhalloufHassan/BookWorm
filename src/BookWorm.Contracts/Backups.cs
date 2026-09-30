using System.ComponentModel.DataAnnotations;

namespace BookWorm.Contracts;

public enum BackupFrequency
{
    Daily,
    Weekly,
}

public enum BackupKind
{
    /// <summary>Made by the schedule or with "Back up now".</summary>
    Created,

    /// <summary>Uploaded to restore from.</summary>
    Uploaded,
}

public sealed record BackupSettings(
    bool Enabled,
    BackupFrequency Frequency,
    DayOfWeek DayOfWeek,
    TimeOnly TimeOfDay,
    string TimeZone,
    int KeepCount);

public sealed class UpdateBackupSettingsRequest
{
    public bool Enabled { get; set; }

    [EnumDataType(typeof(BackupFrequency))]
    public BackupFrequency Frequency { get; set; }

    /// <summary>For weekly backups.</summary>
    [EnumDataType(typeof(DayOfWeek))]
    public DayOfWeek DayOfWeek { get; set; }

    public TimeOnly TimeOfDay { get; set; }

    /// <summary>IANA time zone the time of day is in, e.g. "Europe/Berlin".</summary>
    [Required]
    [StringLength(100)]
    public string TimeZone { get; set; } = "UTC";

    /// <summary>How many backups made by BookWorm to keep; older ones are deleted.</summary>
    [Range(1, 365)]
    public int KeepCount { get; set; } = 7;
}

public sealed record BackupFile(string Name, BackupKind Kind, DateTimeOffset CreatedAt, long SizeBytes);

/// <summary>How the last backup or restore went.</summary>
public sealed record BackupRunResult(
    DateTimeOffset StartedAt,
    DateTimeOffset? FinishedAt,
    bool Succeeded,
    string Message,
    string FileName);

/// <param name="ToolsProblem">Why backups can't run on this server, e.g. missing PostgreSQL tools; null when fine.</param>
/// <param name="Folder">Where the backups are stored on the server (inside the container).</param>
public sealed record BackupOverview(
    BackupSettings Settings,
    bool IsRunning,
    DateTimeOffset? NextRunAt,
    BackupRunResult LastRun,
    BackupRunResult LastRestore,
    IReadOnlyList<BackupFile> Backups,
    string ToolsProblem,
    string Folder);

/// <summary>Confirms a restore; the body makes browsers ask before sending it from another site.</summary>
public sealed class RestoreBackupRequest
{
    [Range(typeof(bool), "true", "true", ErrorMessage = "Confirm that everything will be replaced by the backup.")]
    public bool Confirm { get; set; }
}
