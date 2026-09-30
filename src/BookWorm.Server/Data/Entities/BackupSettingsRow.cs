using BookWorm.Contracts;

namespace BookWorm.Server.Data;

/// <summary>The backup schedule. There is exactly one row, with <see cref="SingletonId"/>.</summary>
public sealed class BackupSettingsRow
{
    public const int SingletonId = 1;

    public int Id { get; set; } = SingletonId;
    public bool Enabled { get; set; } = true;
    public BackupFrequency Frequency { get; set; } = BackupFrequency.Daily;
    public DayOfWeek DayOfWeek { get; set; } = DayOfWeek.Sunday;
    public TimeOnly TimeOfDay { get; set; } = new(3, 0);
    public string TimeZone { get; set; } = "UTC";
    public int KeepCount { get; set; } = 7;

    /// <summary>How the last backup went, shown on the backups page.</summary>
    public DateTimeOffset? LastRunStartedAt { get; set; }
    public DateTimeOffset? LastRunFinishedAt { get; set; }
    public bool? LastRunSucceeded { get; set; }
    public string LastRunMessage { get; set; }
    public string LastRunFile { get; set; }
}
