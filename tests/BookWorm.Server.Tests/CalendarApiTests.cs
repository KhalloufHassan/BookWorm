using System.Net;
using BookWorm.Contracts;
using BookWorm.Server.Data;
using BookWorm.Server.Tests.Infrastructure;
using BookWorm.UI.Api;
using BookWorm.UI.Pages.Calendar;
using Microsoft.Extensions.DependencyInjection;

namespace BookWorm.Server.Tests;

public sealed class CalendarApiTests(BookWormAppFactory app)
{
    [Fact]
    public async Task Calendar_ListsFinishedReadsOverlappingTheRange()
    {
        var api = (await app.CreateUserAsync()).Api;
        var hobb = await api.AddAuthorAsync("Robin Hobb");
        var spanning = await api.AddBookAsync("Assassin's Apprentice", [hobb.Id]);
        var inside = await api.AddBookAsync("Piranesi");
        var before = await api.AddBookAsync("Dune");
        var unfinished = await api.AddBookAsync("Middlemarch");

        await AddReadAsync(spanning.Id, Utc(2025, 2, 25), Utc(2025, 3, 4));
        await AddReadAsync(inside.Id, null, Utc(2025, 3, 20));
        await AddReadAsync(before.Id, Utc(2025, 2, 1), Utc(2025, 2, 28));
        await AddReadAsync(unfinished.Id, Utc(2025, 3, 10), null, ReadStatus.CurrentlyReading);

        var reads = await api.GetCalendarAsync(new DateOnly(2025, 3, 1), new DateOnly(2025, 3, 31), "UTC");

        Assert.Equal(["Assassin's Apprentice", "Piranesi"], reads.Select(r => r.Title));
        Assert.Equal(new DateOnly(2025, 2, 25), reads[0].Started);
        Assert.Equal(new DateOnly(2025, 3, 4), reads[0].Finished);
        Assert.Equal("Robin Hobb", Assert.Single(reads[0].Authors).Name);
        Assert.Null(reads[1].Started);
    }

    [Fact]
    public async Task Calendar_UsesTheAskedTimeZoneForDays()
    {
        var api = (await app.CreateUserAsync()).Api;
        var book = await api.AddBookAsync("Piranesi");
        await AddReadAsync(book.Id, null, new DateTimeOffset(2025, 3, 31, 23, 30, 0, TimeSpan.Zero));

        var inUtc = await api.GetCalendarAsync(new DateOnly(2025, 4, 1), new DateOnly(2025, 4, 30), "UTC");
        var inBerlin = await api.GetCalendarAsync(new DateOnly(2025, 4, 1), new DateOnly(2025, 4, 30), "Europe/Berlin");

        Assert.Empty(inUtc);
        Assert.Equal(new DateOnly(2025, 4, 1), Assert.Single(inBerlin).Finished);
    }

    [Fact]
    public async Task Calendar_RejectsBadRanges()
    {
        var api = (await app.CreateUserAsync()).Api;

        var backwards = await Assert.ThrowsAsync<ApiException>(() => api.GetCalendarAsync(new DateOnly(2025, 3, 2), new DateOnly(2025, 3, 1), "UTC"));
        var tooLong = await Assert.ThrowsAsync<ApiException>(() => api.GetCalendarAsync(new DateOnly(2024, 1, 1), new DateOnly(2025, 1, 1), "UTC"));
        var year = await api.GetCalendarAsync(new DateOnly(2024, 1, 1), new DateOnly(2024, 12, 31), "UTC");

        Assert.Equal(HttpStatusCode.BadRequest, backwards.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, tooLong.StatusCode);
        Assert.Empty(year);
    }

    [Fact]
    public void Layout_PutsOverlappingReadsInSeparateLanes()
    {
        var monday = new DateOnly(2025, 3, 3);
        CalendarRead[] reads =
        [
            Read("Long", monday.AddDays(-5), monday.AddDays(3)),
            Read("Short", monday.AddDays(1), monday.AddDays(2)),
            Read("Later", monday.AddDays(4), monday.AddDays(9)),
            Read("One day", null, monday.AddDays(6)),
        ];

        var bars = CalendarLayout.Week(monday, reads).ToDictionary(b => b.Read.Title);

        Assert.Equal(new CalendarBar(reads[0], 0, 4, 0, false, true), bars["Long"]);
        Assert.Equal(new CalendarBar(reads[1], 1, 2, 1, true, true), bars["Short"]);
        Assert.Equal(new CalendarBar(reads[2], 4, 3, 0, true, false), bars["Later"]);
        Assert.Equal(new CalendarBar(reads[3], 6, 1, 1, true, true), bars["One day"]);
    }

    [Fact]
    public void Layout_ShowsTheWeeksOfAMonth()
    {
        var weeks = CalendarLayout.MonthWeeks(2025, 3);

        Assert.Equal(new DateOnly(2025, 2, 24), weeks[0]);
        Assert.Equal(new DateOnly(2025, 3, 31), weeks[^1]);
        Assert.Equal(6, weeks.Count);
    }

    private static DateTimeOffset Utc(int year, int month, int day) => new(year, month, day, 12, 0, 0, TimeSpan.Zero);

    private static CalendarRead Read(string title, DateOnly? started, DateOnly finished) =>
        new(Guid.NewGuid(), Guid.NewGuid(), title, [], null, null, started, finished);

    private async Task AddReadAsync(Guid bookId, DateTimeOffset? started, DateTimeOffset? finished, ReadStatus status = ReadStatus.Finished)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.Reads.Add(new Read { BookId = bookId, Status = status, StartedAt = started, FinishedAt = finished });
        await db.SaveChangesAsync();
    }
}
