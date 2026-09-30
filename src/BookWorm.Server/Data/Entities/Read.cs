using BookWorm.Contracts;

namespace BookWorm.Server.Data;

/// <summary>One read-through of a book. A book can be read (or skipped) several times.</summary>
public sealed class Read : IAuditable
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid BookId { get; set; }

    public ReadStatus Status { get; set; } = ReadStatus.CurrentlyReading;
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }

    /// <summary>Where the reader left off: the file, the location in it, and the overall progress (0–1).</summary>
    public Guid? FileId { get; set; }
    public string Location { get; set; }
    public double? Progress { get; set; }

    /// <summary>When the book was last opened in the reader for this read; drives "continue reading".</summary>
    public DateTimeOffset? LastOpenedAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    /// <inheritdoc cref="Book.Version" />
    public uint Version { get; set; }

    public Book Book { get; set; }
    public BookFile File { get; set; }
    public List<ReadingSession> Sessions { get; } = [];
}
