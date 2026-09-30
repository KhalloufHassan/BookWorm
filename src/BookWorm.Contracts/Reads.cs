using System.ComponentModel.DataAnnotations;

namespace BookWorm.Contracts;

/// <param name="FileId">The file this read was last read in, if it was read in BookWorm's reader.</param>
/// <param name="Location">Where the reader left off in that file (see <see cref="HighlightDetails.Location"/>).</param>
/// <param name="Progress">How far into the book, from 0 to 1.</param>
public sealed record ReadDetails(
    Guid Id,
    Guid BookId,
    ReadStatus Status,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt,
    Guid? FileId,
    string Location,
    double? Progress,
    DateTimeOffset? LastOpenedAt,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    uint Version);

public class CreateReadRequest : IValidatableObject
{
    [EnumDataType(typeof(ReadStatus))]
    public ReadStatus Status { get; set; } = ReadStatus.CurrentlyReading;

    public DateTimeOffset? StartedAt { get; set; }

    /// <summary>When the read was finished or skipped.</summary>
    public DateTimeOffset? FinishedAt { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (Status == ReadStatus.CurrentlyReading && FinishedAt is not null)
        {
            yield return new ValidationResult("A read that is still in progress can't have a finish time.", [nameof(FinishedAt)]);
        }

        if (StartedAt is { } started && FinishedAt is { } finished && finished < started)
        {
            yield return new ValidationResult("The finish time can't be before the start time.", [nameof(FinishedAt)]);
        }
    }
}

public sealed class UpdateReadRequest : CreateReadRequest
{
    /// <inheritdoc cref="UpdateBookRequest.Version" />
    [Required]
    public uint? Version { get; set; }
}

/// <summary>
/// Sent by the reader as you read: saves where you are, and extends the current reading session
/// (used for the reading-time statistics).
/// </summary>
public sealed class ReadProgressRequest
{
    [Required]
    public Guid? FileId { get; set; }

    [Required]
    [StringLength(ApiLimits.LocationMaxLength)]
    public string Location { get; set; } = "";

    [Range(0.0, 1.0)]
    public double Progress { get; set; }

    /// <summary>Chosen by the reader when a sitting starts; the same id extends the same session.</summary>
    public Guid? SessionId { get; set; }

    public DateTimeOffset? SessionStartedAt { get; set; }

    [Range(0.0, 1.0)]
    public double? SessionStartProgress { get; set; }
}

/// <summary>Sent by the reader while you browse a book with no read in progress: saves where you are, nothing else.</summary>
public sealed class BrowsePositionRequest
{
    [Required]
    [StringLength(ApiLimits.LocationMaxLength)]
    public string Location { get; set; } = "";

    [Range(0.0, 1.0)]
    public double Progress { get; set; }
}

/// <summary>Ends a read from the reader, without needing its current version.</summary>
public sealed class FinishReadRequest : IValidatableObject
{
    [EnumDataType(typeof(ReadStatus))]
    public ReadStatus Status { get; set; } = ReadStatus.Finished;

    /// <summary>Defaults to now.</summary>
    public DateTimeOffset? FinishedAt { get; set; }

    /// <summary>Also set the book's own status to Finished or Skipped.</summary>
    public bool UpdateBookStatus { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (Status == ReadStatus.CurrentlyReading)
        {
            yield return new ValidationResult("A read can only be finished or skipped.", [nameof(Status)]);
        }
    }
}

/// <summary>A read in progress, for "continue reading".</summary>
public sealed record CurrentRead(
    ReadDetails Read,
    Guid BookId,
    string Title,
    IReadOnlyList<AuthorRef> Authors,
    long? CoverVersion);
