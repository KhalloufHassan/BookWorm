using System.Globalization;
using BookWorm.Contracts;
using MudBlazor;

namespace BookWorm.UI.Components;

/// <summary>How statuses, dates and names are shown throughout the app.</summary>
public static class Labels
{
    private static readonly CultureInfo Culture = CultureInfo.InvariantCulture;

    public static readonly BookStatus[] BookStatuses = Enum.GetValues<BookStatus>();
    public static readonly ReadStatus[] ReadStatuses = Enum.GetValues<ReadStatus>();

    public static string For(BookStatus status) => status switch
    {
        BookStatus.WantToRead => "Want to read",
        BookStatus.CurrentlyReading => "Currently reading",
        BookStatus.Finished => "Finished",
        BookStatus.Skipped => "Skipped",
        _ => status.ToString(),
    };

    public static string For(ReadStatus status) => status switch
    {
        ReadStatus.CurrentlyReading => "Reading",
        ReadStatus.Finished => "Finished",
        ReadStatus.Skipped => "Skipped",
        _ => status.ToString(),
    };

    public static string IconFor(BookStatus status) => status switch
    {
        BookStatus.WantToRead => Icons.Material.Rounded.BookmarkBorder,
        BookStatus.CurrentlyReading => Icons.Material.Rounded.AutoStories,
        BookStatus.Finished => Icons.Material.Rounded.TaskAlt,
        _ => Icons.Material.Rounded.SkipNext,
    };

    public static string IconFor(ReadStatus status) => status switch
    {
        ReadStatus.CurrentlyReading => Icons.Material.Rounded.AutoStories,
        ReadStatus.Finished => Icons.Material.Rounded.TaskAlt,
        _ => Icons.Material.Rounded.SkipNext,
    };

    public static Color ColorFor(BookStatus status) => status switch
    {
        BookStatus.WantToRead => Color.Tertiary,
        BookStatus.CurrentlyReading => Color.Primary,
        BookStatus.Finished => Color.Success,
        _ => Color.Default,
    };

    public static Color ColorFor(ReadStatus status) => status switch
    {
        ReadStatus.CurrentlyReading => Color.Primary,
        ReadStatus.Finished => Color.Success,
        _ => Color.Default,
    };

    public static readonly CollectionType[] CollectionTypes = Enum.GetValues<CollectionType>();

    public static string For(CollectionType type) => type switch
    {
        CollectionType.Series => "Series",
        CollectionType.Volumes => "Volumes",
        CollectionType.Related => "Related books",
        _ => type.ToString(),
    };

    public static string IconFor(CollectionType type) => type switch
    {
        CollectionType.Series => Icons.Material.Rounded.CollectionsBookmark,
        CollectionType.Volumes => Icons.Material.Rounded.LibraryBooks,
        _ => Icons.Material.Rounded.Hub,
    };

    public static readonly HighlightColor[] HighlightColors = Enum.GetValues<HighlightColor>();

    /// <summary>The highlight colours as CSS, matching the reader (reader-common.js).</summary>
    public static string Css(HighlightColor color) => color switch
    {
        HighlightColor.Green => "#7cc47f",
        HighlightColor.Blue => "#6fa8dc",
        HighlightColor.Pink => "#f08bb0",
        HighlightColor.Purple => "#b39ddb",
        _ => "#f2c544",
    };

    public static string Percent(double? fraction) => fraction is { } f ? $"{Math.Floor(f * 100):0}%" : "";

    /// <summary>"2 h 5 min", "45 min".</summary>
    public static string Minutes(int minutes) => minutes switch
    {
        < 60 => $"{minutes} min",
        _ when minutes % 60 == 0 => $"{minutes / 60} h",
        _ => $"{minutes / 60} h {minutes % 60} min",
    };

    public static string Authors(IReadOnlyList<AuthorRef> authors) =>
        authors.Count == 0 ? "Unknown author" : string.Join(", ", authors.Select(a => a.Name));

    public static string Date(DateOnly? date) => date?.ToString("d MMM yyyy", Culture) ?? "";

    public static string DateTime(DateTimeOffset? value) =>
        value?.ToLocalTime().ToString("d MMM yyyy, HH:mm", Culture) ?? "";

    public static string Rating(decimal rating) => rating.ToString("0.0", Culture);

    /// <summary>"1920 – 1986", "Born 1939" or "Died 1817".</summary>
    public static string Lifespan(DateOnly? born, DateOnly? died) => (born, died) switch
    {
        ({ } b, { } d) => $"{b.Year} – {d.Year}",
        ({ } b, null) => $"Born {b.Year}",
        (null, { } d) => $"Died {d.Year}",
        _ => "",
    };

    public static string Count(int count, string noun) => count == 1 ? $"1 {noun}" : $"{count:N0} {noun}s";

    public static string Initials(string name)
    {
        var parts = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length switch
        {
            0 => "?",
            1 => parts[0][..1].ToUpperInvariant(),
            _ => $"{parts[0][0]}{parts[^1][0]}".ToUpperInvariant(),
        };
    }
}
