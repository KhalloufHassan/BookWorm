namespace BookWorm.Server.Data;

public sealed class Tag : IAuditable
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid UserId { get; set; }

    /// <summary>Unique per user, ignoring case.</summary>
    public required string Name { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    /// <inheritdoc cref="Book.Version" />
    public uint Version { get; set; }

    public List<Book> Books { get; } = [];
}
