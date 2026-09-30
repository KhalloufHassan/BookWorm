namespace BookWorm.Contracts;

/// <summary>
/// Reading statistics for the dashboard. Days, months and years are counted in the time zone the
/// app asked for, so "today" matches the reader's clock.
/// </summary>
public sealed record ReadingStats(
    int Year,
    IReadOnlyList<int> AvailableYears,
    LibraryCounts Library,
    int BooksFinishedInYear,
    IReadOnlyList<int> BooksFinishedByMonth,
    int MinutesInYear,
    IReadOnlyList<int> MinutesByMonth,
    IReadOnlyList<DailyMinutes> LastThirtyDays,
    int MinutesThisWeek,
    int AverageMinutesPerDay,
    int CurrentStreakDays,
    int LongestStreakDays,
    decimal? AverageRating,
    IReadOnlyList<int> RatingDistribution,
    IReadOnlyList<RankedItem> TopAuthors,
    IReadOnlyList<RankedItem> TopTags);

public sealed record LibraryCounts(
    int Books,
    int WantToRead,
    int CurrentlyReading,
    int Finished,
    int Skipped,
    int Authors,
    int Highlights);

public sealed record DailyMinutes(DateOnly Date, int Minutes);

/// <summary>An author or tag with how many books it counts for.</summary>
public sealed record RankedItem(Guid Id, string Name, int Count);
