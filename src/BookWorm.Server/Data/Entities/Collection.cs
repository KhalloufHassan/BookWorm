using BookWorm.Contracts;

namespace BookWorm.Server.Data;

/// <summary>A named, ordered group of books: a series, the volumes of a set, or related books.</summary>
public sealed class Collection : IAuditable
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid UserId { get; set; }

    /// <summary>Unique per user, ignoring case.</summary>
    public required string Name { get; set; }
    public CollectionType Type { get; set; } = CollectionType.Series;

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    /// <inheritdoc cref="Book.Version" />
    public uint Version { get; set; }

    /// <summary>The collection's books; <see cref="CollectionBook.Position"/> gives the order.</summary>
    public List<CollectionBook> Books { get; } = [];
}
