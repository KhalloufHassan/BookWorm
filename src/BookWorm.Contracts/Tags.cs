using System.ComponentModel.DataAnnotations;

namespace BookWorm.Contracts;

public sealed record TagSummary(
    Guid Id,
    string Name,
    int BookCount,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    uint Version);

public class CreateTagRequest
{
    /// <summary>Tag names are unique per user, ignoring case.</summary>
    [Required]
    [StringLength(ApiLimits.TagNameMaxLength)]
    public string Name { get; set; } = "";
}

public sealed class UpdateTagRequest : CreateTagRequest
{
    /// <inheritdoc cref="UpdateBookRequest.Version" />
    [Required]
    public uint? Version { get; set; }
}
