using BookWorm.Contracts;
using BookWorm.Server.Data;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace BookWorm.Server.Api;

internal static class StatsEndpoints
{
    /// <summary>A day counts toward a streak with at least this much reading.</summary>
    private const int StreakMinimumMinutes = 1;

    /// <summary>The calendar shows at most a year at a time.</summary>
    private const int CalendarMaxDays = 366;

    public static void MapStatsEndpoints(this IEndpointRouteBuilder api)
    {
        api.MapGet("/stats", GetStats).WithTags("Statistics")
            .WithSummary("Reading statistics for a year. Pass your IANA time zone (e.g. Europe/Berlin) so days match your clock.");
        api.MapGet("/calendar", GetCalendar).WithTags("Statistics")
            .WithSummary("Finished reads between two dates (inclusive) that were in progress or finished in that range, for the reading calendar.");
    }

    private static async Task<Results<Ok<ReadingStats>, ValidationProblem>> GetStats(
        [FromQuery] int? year,
        [FromQuery] string timeZone,
        AppDbContext db,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        if (!TryFindTimeZone(timeZone, out var zone))
        {
            return ApiErrors.Validation("timeZone", "Unknown time zone.");
        }

        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(timeProvider.GetUtcNow(), zone).DateTime);
        var selectedYear = year is >= 1900 and <= 9999 ? year.Value : today.Year;

        var statusCounts = await db.Books.GroupBy(b => b.Status)
            .Select(g => new { Status = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Status, x => x.Count, cancellationToken);
        var library = new LibraryCounts(
            statusCounts.Values.Sum(),
            statusCounts.GetValueOrDefault(BookStatus.WantToRead),
            statusCounts.GetValueOrDefault(BookStatus.CurrentlyReading),
            statusCounts.GetValueOrDefault(BookStatus.Finished),
            statusCounts.GetValueOrDefault(BookStatus.Skipped),
            await db.Authors.CountAsync(cancellationToken),
            await db.Highlights.CountAsync(cancellationToken));

        // Finished reads and reading sessions are few enough per person to bucket in memory,
        // which keeps the time zone handling simple and exact.
        var finishedReads = await db.Reads.AsNoTracking()
            .Where(r => r.Status == ReadStatus.Finished && r.FinishedAt != null)
            .Select(r => new { r.BookId, FinishedAt = r.FinishedAt.Value })
            .ToListAsync(cancellationToken);
        var sessions = await db.ReadingSessions.AsNoTracking()
            .Select(s => new { s.StartedAt, s.EndedAt })
            .ToListAsync(cancellationToken);

        DateOnly LocalDate(DateTimeOffset instant) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, zone).DateTime);

        var finishedLocal = finishedReads.Select(r => (r.BookId, Date: LocalDate(r.FinishedAt))).ToList();
        var minutesByDay = new Dictionary<DateOnly, double>();
        foreach (var session in sessions)
        {
            // Sessions are short; attributing each to the day it started is accurate enough.
            var day = LocalDate(session.StartedAt);
            minutesByDay[day] = minutesByDay.GetValueOrDefault(day) + Math.Max(0, (session.EndedAt - session.StartedAt).TotalMinutes);
        }

        var years = finishedLocal.Select(r => r.Date.Year)
            .Concat(minutesByDay.Keys.Select(d => d.Year))
            .Append(today.Year)
            .Distinct()
            .OrderDescending()
            .ToList();

        var booksFinishedByMonth = new int[12];
        foreach (var month in finishedLocal.Where(r => r.Date.Year == selectedYear).GroupBy(r => r.Date.Month))
        {
            booksFinishedByMonth[month.Key - 1] = month.Select(r => r.BookId).Distinct().Count();
        }

        var minutesByMonth = new double[12];
        foreach (var (day, minutes) in minutesByDay.Where(d => d.Key.Year == selectedYear))
        {
            minutesByMonth[day.Month - 1] += minutes;
        }

        var lastThirtyDays = Enumerable.Range(0, 30)
            .Select(offset => today.AddDays(offset - 29))
            .Select(day => new DailyMinutes(day, (int)Math.Round(minutesByDay.GetValueOrDefault(day))))
            .ToList();

        var weekStart = today.AddDays(-(((int)today.DayOfWeek + 6) % 7));
        var minutesThisWeek = minutesByDay.Where(d => d.Key >= weekStart && d.Key <= today).Sum(d => d.Value);

        var (currentStreak, longestStreak) = Streaks(
            minutesByDay.Where(d => d.Value >= StreakMinimumMinutes).Select(d => d.Key).ToHashSet(), today);

        var ratings = await db.Books.Where(b => b.Rating != null).Select(b => b.Rating.Value).ToListAsync(cancellationToken);
        var ratingDistribution = new int[5];
        foreach (var rating in ratings)
        {
            ratingDistribution[Math.Clamp((int)Math.Ceiling(rating) - 1, 0, 4)]++;
        }

        var topAuthors = await db.Authors
            .Select(a => new { a.Id, a.Name, Count = a.Books.Count(ba => ba.Book.Status == BookStatus.Finished) })
            .Where(a => a.Count > 0)
            .OrderByDescending(a => a.Count).ThenBy(a => a.Name)
            .Take(5)
            .ToListAsync(cancellationToken);
        var topTags = await db.Tags
            .Select(t => new { t.Id, t.Name, Count = t.Books.Count })
            .Where(t => t.Count > 0)
            .OrderByDescending(t => t.Count).ThenBy(t => t.Name)
            .Take(5)
            .ToListAsync(cancellationToken);

        return TypedResults.Ok(new ReadingStats(
            selectedYear,
            years,
            library,
            finishedLocal.Where(r => r.Date.Year == selectedYear).Select(r => r.BookId).Distinct().Count(),
            booksFinishedByMonth,
            (int)Math.Round(minutesByMonth.Sum()),
            minutesByMonth.Select(m => (int)Math.Round(m)).ToList(),
            lastThirtyDays,
            (int)Math.Round(minutesThisWeek),
            (int)Math.Round(lastThirtyDays.Sum(d => d.Minutes) / 30.0),
            currentStreak,
            longestStreak,
            ratings.Count == 0 ? null : Math.Round(ratings.Average(), 1, MidpointRounding.AwayFromZero),
            ratingDistribution,
            topAuthors.Select(a => new RankedItem(a.Id, a.Name, a.Count)).ToList(),
            topTags.Select(t => new RankedItem(t.Id, t.Name, t.Count)).ToList()));
    }

    private static async Task<Results<Ok<List<CalendarRead>>, ValidationProblem>> GetCalendar(
        [FromQuery] DateOnly from,
        [FromQuery] DateOnly to,
        [FromQuery] string timeZone,
        AppDbContext db,
        CancellationToken cancellationToken)
    {
        if (!TryFindTimeZone(timeZone, out var zone))
        {
            return ApiErrors.Validation("timeZone", "Unknown time zone.");
        }

        if (to < from || to.DayNumber - from.DayNumber >= CalendarMaxDays)
        {
            return ApiErrors.Validation("to", $"Pick an end date on or after the start date, at most {CalendarMaxDays} days later.");
        }

        // A day of slack either side covers every UTC offset; the exact local dates are checked below.
        var earliest = new DateTimeOffset(from.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero).AddDays(-1);
        var latest = new DateTimeOffset(to.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero).AddDays(2);

        var reads = await db.Reads.AsNoTracking()
            .Where(r => r.Status == ReadStatus.Finished && r.FinishedAt != null)
            .Where(r => r.FinishedAt >= earliest && (r.StartedAt ?? r.FinishedAt) < latest)
            .Select(r => new
            {
                r.Id,
                r.BookId,
                r.Book.Title,
                Authors = r.Book.Authors.OrderBy(ba => ba.Position).Select(ba => new AuthorRef(ba.Author.Id, ba.Author.Name)).ToList(),
                r.Book.CoverUpdatedAt,
                r.Book.Rating,
                r.StartedAt,
                FinishedAt = r.FinishedAt.Value,
            })
            .AsSplitQuery()
            .ToListAsync(cancellationToken);

        DateOnly LocalDate(DateTimeOffset instant) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, zone).DateTime);

        var calendar = reads
            .Select(r =>
            {
                var finished = LocalDate(r.FinishedAt);
                DateOnly? started = r.StartedAt is { } s ? LocalDate(s) : null;

                // A start after the finish is a typing mistake; show the read on its finish day only.
                if (started > finished)
                {
                    started = null;
                }

                return new CalendarRead(r.Id, r.BookId, r.Title, r.Authors, Covers.Version(r.CoverUpdatedAt), r.Rating, started, finished);
            })
            .Where(r => r.Finished >= from && (r.Started ?? r.Finished) <= to)
            .OrderBy(r => r.Started ?? r.Finished)
            .ThenBy(r => r.Finished)
            .ThenBy(r => r.Title)
            .ToList();

        return TypedResults.Ok(calendar);
    }

    /// <summary>
    /// The current streak counts back from today, or from yesterday when today has no reading yet,
    /// so a streak isn't "broken" in the morning before you've read.
    /// </summary>
    internal static (int Current, int Longest) Streaks(HashSet<DateOnly> days, DateOnly today)
    {
        var current = 0;
        var day = days.Contains(today) ? today : today.AddDays(-1);
        while (days.Contains(day))
        {
            current++;
            day = day.AddDays(-1);
        }

        var longest = 0;
        foreach (var start in days.Where(d => !days.Contains(d.AddDays(-1))))
        {
            var length = 0;
            for (var d = start; days.Contains(d); d = d.AddDays(1))
            {
                length++;
            }

            longest = Math.Max(longest, length);
        }

        return (current, longest);
    }

    private static bool TryFindTimeZone(string id, out TimeZoneInfo zone)
    {
        zone = TimeZoneInfo.Utc;
        if (string.IsNullOrWhiteSpace(id))
        {
            return true;
        }

        return TimeZoneInfo.TryFindSystemTimeZoneById(id, out zone);
    }
}
