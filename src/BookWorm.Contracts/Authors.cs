using System.ComponentModel.DataAnnotations;

namespace BookWorm.Contracts;

public sealed record AuthorSummary(
    Guid Id,
    string Name,
    DateOnly? BirthDate,
    DateOnly? DeathDate,
    int BookCount,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record BookRef(Guid Id, string Title, BookStatus Status, decimal? Rating);

public sealed record AuthorDetails(
    Guid Id,
    string Name,
    DateOnly? BirthDate,
    DateOnly? DeathDate,
    IReadOnlyList<BookRef> Books,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    uint Version);

public class CreateAuthorRequest : IValidatableObject
{
    [Required]
    [StringLength(ApiLimits.AuthorNameMaxLength)]
    public string Name { get; set; } = "";

    public DateOnly? BirthDate { get; set; }
    public DateOnly? DeathDate { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (BirthDate is { } born && DeathDate is { } died && died < born)
        {
            yield return new ValidationResult("Date of death can't be before date of birth.", [nameof(DeathDate)]);
        }
    }
}

public sealed class UpdateAuthorRequest : CreateAuthorRequest
{
    /// <inheritdoc cref="UpdateBookRequest.Version" />
    [Required]
    public uint? Version { get; set; }
}

public enum AuthorSort
{
    Name,
    BirthDate,
    DeathDate,
    BookCount,
    CreatedAt,

    /// <summary>Best match first; only meaningful together with a search term.</summary>
    Relevance,
}

/// <summary>Query string parameters for <c>GET /api/authors</c>.</summary>
public sealed class AuthorListQuery
{
    /// <summary>Words to look for in the name; ignores case and accents and tolerates small typos.</summary>
    public string Search { get; set; }

    public DateOnly? BornFrom { get; set; }
    public DateOnly? BornTo { get; set; }
    public DateOnly? DiedFrom { get; set; }
    public DateOnly? DiedTo { get; set; }

    /// <summary>Defaults to <see cref="AuthorSort.Relevance"/> when searching, otherwise <see cref="AuthorSort.Name"/>.</summary>
    public AuthorSort? Sort { get; set; }
    public SortDirection? Direction { get; set; }

    public int? Page { get; set; }
    public int? PageSize { get; set; }
}
