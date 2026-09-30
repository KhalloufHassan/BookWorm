namespace BookWorm.Server.Data;

public sealed class Author : IAuditable
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid UserId { get; set; }

    public required string Name { get; set; }
    public DateOnly? BirthDate { get; set; }
    public DateOnly? DeathDate { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    /// <inheritdoc cref="Book.Version" />
    public uint Version { get; set; }

    public List<BookAuthor> Books { get; } = [];
}
