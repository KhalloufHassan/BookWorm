using BookWorm.Contracts;

namespace BookWorm.UI.Pages.Calendar;

/// <summary>A read's bar within one week of the month grid.</summary>
/// <param name="FirstColumn">The weekday it starts on in this week, 0 = Monday.</param>
/// <param name="Span">How many days of this week it covers.</param>
/// <param name="Lane">Its row within the week; bars in different lanes never overlap.</param>
/// <param name="StartsHere">The read started this week (or has no start date), so the bar has a left end.</param>
/// <param name="EndsHere">The read finished this week, so the bar has a right end.</param>
public sealed record CalendarBar(CalendarRead Read, int FirstColumn, int Span, int Lane, bool StartsHere, bool EndsHere);

/// <summary>Lays reads out as bars on the weeks of a month grid.</summary>
public static class CalendarLayout
{
    /// <summary>The Monday on or before <paramref name="day"/>.</summary>
    public static DateOnly WeekStart(DateOnly day) => day.AddDays(-(((int)day.DayOfWeek + 6) % 7));

    /// <summary>The weeks (as their Mondays) shown for a month: from the week of the 1st to the week of the last day.</summary>
    public static List<DateOnly> MonthWeeks(int year, int month)
    {
        var first = new DateOnly(year, month, 1);
        var last = first.AddMonths(1).AddDays(-1);
        var weeks = new List<DateOnly>();
        for (var week = WeekStart(first); week <= last; week = week.AddDays(7))
        {
            weeks.Add(week);
        }

        return weeks;
    }

    /// <summary>
    /// The bars of one week. Longer reads get the top lanes so they stay in place from week to week
    /// as much as possible; each bar takes the first lane free for all of its days.
    /// </summary>
    public static List<CalendarBar> Week(DateOnly monday, IEnumerable<CalendarRead> reads)
    {
        var sunday = monday.AddDays(6);
        var lanes = new List<bool[]>();
        var bars = new List<CalendarBar>();

        var inWeek = reads
            .Where(r => r.Finished >= monday && (r.Started ?? r.Finished) <= sunday)
            .OrderBy(r => Max(r.Started ?? r.Finished, monday))
            .ThenByDescending(r => r.Finished.DayNumber - (r.Started ?? r.Finished).DayNumber)
            .ThenBy(r => r.Title);

        foreach (var read in inWeek)
        {
            var start = read.Started ?? read.Finished;
            var first = Max(start, monday).DayNumber - monday.DayNumber;
            var last = Min(read.Finished, sunday).DayNumber - monday.DayNumber;

            var lane = lanes.FindIndex(used => !used[first..(last + 1)].Any(u => u));
            if (lane < 0)
            {
                lane = lanes.Count;
                lanes.Add(new bool[7]);
            }

            for (var day = first; day <= last; day++)
            {
                lanes[lane][day] = true;
            }

            bars.Add(new CalendarBar(read, first, last - first + 1, lane, start >= monday, read.Finished <= sunday));
        }

        return bars;
    }

    private static DateOnly Max(DateOnly a, DateOnly b) => a > b ? a : b;

    private static DateOnly Min(DateOnly a, DateOnly b) => a < b ? a : b;
}
