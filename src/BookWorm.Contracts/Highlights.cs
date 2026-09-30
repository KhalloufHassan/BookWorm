using System.ComponentModel.DataAnnotations;

namespace BookWorm.Contracts;

public enum HighlightColor
{
    Yellow,
    Green,
    Blue,
    Pink,
    Purple,
}

/// <summary>Whether a highlight can be shown in the book file it belongs to.</summary>
public enum HighlightState
{
    /// <summary>Its location points at the right text in the current file.</summary>
    Anchored,

    /// <summary>
    /// The file was replaced since the highlight was made. The reader looks for the highlighted text
    /// in the new file the next time the book is opened, and reports the result.
    /// </summary>
    NeedsCheck,

    /// <summary>The text couldn't be found in the current file (or the file was deleted). Kept with its note.</summary>
    Missing,
}

/// <summary>
/// A highlighted passage of a book file, optionally with a note. Stored in the database; the book
/// file itself is never changed.
/// </summary>
/// <param name="FileId">The file the highlight was made in; null once that file is deleted.</param>
/// <param name="Location">
/// Where the passage is in the file: an EPUB CFI for the ebook formats, <c>page:N</c> for PDFs.
/// </param>
/// <param name="Prefix">A little text just before the passage, used to find it again in a new version of the file.</param>
/// <param name="Suffix">A little text just after the passage.</param>
/// <param name="Position">How far into the book the passage is, from 0 to 1. Used for sorting.</param>
public sealed record HighlightDetails(
    Guid Id,
    Guid BookId,
    Guid? FileId,
    BookFormat Format,
    string Location,
    string Text,
    string Prefix,
    string Suffix,
    string Chapter,
    string PageLabel,
    double Position,
    HighlightColor Color,
    string Note,
    HighlightState State,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    uint Version);

public sealed class CreateHighlightRequest
{
    [Required]
    public Guid? FileId { get; set; }

    [Required]
    [StringLength(ApiLimits.LocationMaxLength)]
    public string Location { get; set; } = "";

    [Required]
    [StringLength(ApiLimits.HighlightTextMaxLength)]
    public string Text { get; set; } = "";

    [StringLength(ApiLimits.HighlightContextMaxLength)]
    public string Prefix { get; set; }

    [StringLength(ApiLimits.HighlightContextMaxLength)]
    public string Suffix { get; set; }

    [StringLength(ApiLimits.ChapterMaxLength)]
    public string Chapter { get; set; }

    [StringLength(ApiLimits.PageLabelMaxLength)]
    public string PageLabel { get; set; }

    [Range(0.0, 1.0)]
    public double Position { get; set; }

    [EnumDataType(typeof(HighlightColor))]
    public HighlightColor Color { get; set; } = HighlightColor.Yellow;

    /// <summary>Your note about the passage, in Markdown.</summary>
    [StringLength(ApiLimits.HighlightNoteMaxLength)]
    public string Note { get; set; }
}

public sealed class UpdateHighlightRequest
{
    [EnumDataType(typeof(HighlightColor))]
    public HighlightColor Color { get; set; }

    [StringLength(ApiLimits.HighlightNoteMaxLength)]
    public string Note { get; set; }

    /// <inheritdoc cref="UpdateBookRequest.Version" />
    [Required]
    public uint? Version { get; set; }
}

/// <summary>
/// Sent by the reader after looking for a <see cref="HighlightState.NeedsCheck"/> highlight in the
/// current version of its file.
/// </summary>
public sealed class ReanchorHighlightRequest : IValidatableObject
{
    [Required]
    public Guid? FileId { get; set; }

    public bool Found { get; set; }

    [StringLength(ApiLimits.LocationMaxLength)]
    public string Location { get; set; }

    [StringLength(ApiLimits.ChapterMaxLength)]
    public string Chapter { get; set; }

    [StringLength(ApiLimits.PageLabelMaxLength)]
    public string PageLabel { get; set; }

    [Range(0.0, 1.0)]
    public double? Position { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (Found && string.IsNullOrWhiteSpace(Location))
        {
            yield return new ValidationResult("A found highlight needs its new location.", [nameof(Location)]);
        }
    }
}
