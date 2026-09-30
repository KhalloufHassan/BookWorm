namespace BookWorm.Server.Data;

/// <summary>One sitting with a book in the reader, for the reading-time statistics.</summary>
public sealed class ReadingSession
{
    public Guid Id { get; set; }
    public Guid ReadId { get; set; }
    public Guid? FileId { get; set; }

    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset EndedAt { get; set; }
    public double? StartProgress { get; set; }
    public double? EndProgress { get; set; }

    public Read Read { get; set; }
}
