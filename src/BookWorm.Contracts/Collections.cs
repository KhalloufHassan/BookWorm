using System.ComponentModel.DataAnnotations;

namespace BookWorm.Contracts;

/// <summary>What kind of group a collection is. Only a label; every type behaves the same.</summary>
public enum CollectionType
{
    Series,
    Volumes,
    Related,
}

public sealed record CollectionRef(Guid Id, string Name, CollectionType Type);

public sealed record CollectionSummary(
    Guid Id,
    string Name,
    CollectionType Type,
    int BookCount,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    uint Version);

/// <param name="Books">The collection's books, in the owner's order.</param>
public sealed record CollectionDetails(
    Guid Id,
    string Name,
    CollectionType Type,
    IReadOnlyList<BookSummary> Books,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    uint Version);

public class CreateCollectionRequest
{
    /// <summary>Collection names are unique per user, ignoring case.</summary>
    [Required]
    [StringLength(ApiLimits.CollectionNameMaxLength)]
    public string Name { get; set; } = "";

    [EnumDataType(typeof(CollectionType))]
    public CollectionType Type { get; set; } = CollectionType.Series;
}

public sealed class UpdateCollectionRequest : CreateCollectionRequest
{
    /// <inheritdoc cref="UpdateBookRequest.Version" />
    [Required]
    public uint? Version { get; set; }
}

public sealed class SetCollectionBooksRequest
{
    /// <summary>Every book in the collection, in order. Books left out are removed from it (they stay in the library).</summary>
    [MaxLength(ApiLimits.MaxBooksPerCollection)]
    public List<Guid> BookIds { get; set; } = [];
}
