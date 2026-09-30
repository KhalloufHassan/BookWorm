using System.Net;
using BookWorm.Contracts;
using BookWorm.Server.Api;
using BookWorm.Server.Data;
using BookWorm.Server.Tests.Infrastructure;
using BookWorm.UI.Api;
using Microsoft.Extensions.DependencyInjection;

namespace BookWorm.Server.Tests;

public sealed class StatsApiTests(BookWormAppFactory app)
{
    [Fact]
    public async Task Stats_CountFinishedBooksByMonth()
    {
        var api = (await app.CreateUserAsync()).Api;
        var hobb = await api.AddAuthorAsync("Robin Hobb");
        var clarke = await api.AddAuthorAsync("Susanna Clarke");
        var one = await api.AddBookAsync("Assassin's Apprentice", [hobb.Id], status: BookStatus.Finished, rating: 4.3m);
        var two = await api.AddBookAsync("Royal Assassin", [hobb.Id], status: BookStatus.Finished, rating: 4.8m);
        var three = await api.AddBookAsync("Piranesi", [clarke.Id], status: BookStatus.Finished, rating: 5m);
        await api.AddBookAsync("Dune", status: BookStatus.WantToRead, rating: 0.5m);

        await AddFinishedReadAsync(one.Id, new DateTimeOffset(2025, 3, 10, 12, 0, 0, TimeSpan.Zero));
        await AddFinishedReadAsync(two.Id, new DateTimeOffset(2025, 3, 20, 12, 0, 0, TimeSpan.Zero));
        await AddFinishedReadAsync(three.Id, new DateTimeOffset(2025, 7, 1, 12, 0, 0, TimeSpan.Zero));
        await AddFinishedReadAsync(three.Id, new DateTimeOffset(2024, 1, 5, 12, 0, 0, TimeSpan.Zero));

        var stats = await api.GetStatsAsync(2025, "UTC");

        Assert.Equal(2025, stats.Year);
        Assert.Contains(2025, stats.AvailableYears);
        Assert.Contains(2024, stats.AvailableYears);
        Assert.Equal(3, stats.BooksFinishedInYear);
        Assert.Equal(2, stats.BooksFinishedByMonth[2]);
        Assert.Equal(1, stats.BooksFinishedByMonth[6]);
        Assert.Equal(new LibraryCounts(4, 1, 0, 3, 0, 2, 0), stats.Library);
        Assert.Equal([1, 0, 0, 0, 3], stats.RatingDistribution);
        Assert.Equal(3.7m, stats.AverageRating);
        Assert.Equal(new RankedItem(hobb.Id, "Robin Hobb", 2), stats.TopAuthors[0]);
    }

    [Fact]
    public async Task Stats_AddUpReadingTimeAndStreaks()
    {
        var api = (await app.CreateUserAsync()).Api;
        var book = await api.AddBookAsync("Middlemarch");
        var read = await api.StartOrResumeReadAsync(book.Id);
        var today = new DateTimeOffset(DateTime.UtcNow.Date, TimeSpan.Zero);

        await AddSessionAsync(read.Id, today.AddMinutes(1), minutes: 30);
        await AddSessionAsync(read.Id, today.AddDays(-1).AddHours(20), minutes: 10);
        await AddSessionAsync(read.Id, today.AddDays(-2).AddHours(8), minutes: 5);
        await AddSessionAsync(read.Id, today.AddDays(-5).AddHours(8), minutes: 20);

        var stats = await api.GetStatsAsync(null, "UTC");

        Assert.Equal(30, stats.LastThirtyDays[^1].Minutes);
        Assert.Equal(10, stats.LastThirtyDays[^2].Minutes);
        Assert.Equal(DateOnly.FromDateTime(today.UtcDateTime), stats.LastThirtyDays[^1].Date);
        Assert.Equal(3, stats.CurrentStreakDays);
        Assert.Equal(3, stats.LongestStreakDays);
        Assert.Equal(2, stats.AverageMinutesPerDay); // 65 minutes over 30 days
    }

    [Fact]
    public async Task Stats_RejectUnknownTimeZones()
    {
        var api = (await app.CreateUserAsync()).Api;

        var error = await Assert.ThrowsAsync<ApiException>(() => api.GetStatsAsync(null, "Mars/Olympus_Mons"));

        Assert.Equal(HttpStatusCode.BadRequest, error.StatusCode);
    }

    [Fact]
    public void Streaks_ToleratesNotHavingReadYetToday()
    {
        var today = new DateOnly(2026, 9, 29);
        HashSet<DateOnly> days = [today.AddDays(-1), today.AddDays(-2), today.AddDays(-10), today.AddDays(-11), today.AddDays(-12), today.AddDays(-13)];

        var (current, longest) = StatsEndpoints.Streaks(days, today);

        Assert.Equal(2, current);
        Assert.Equal(4, longest);
    }

    private async Task AddFinishedReadAsync(Guid bookId, DateTimeOffset finished)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.Reads.Add(new Read { BookId = bookId, Status = ReadStatus.Finished, StartedAt = finished.AddDays(-7), FinishedAt = finished });
        await db.SaveChangesAsync();
    }

    private async Task AddSessionAsync(Guid readId, DateTimeOffset started, int minutes)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.ReadingSessions.Add(new ReadingSession
        {
            Id = Guid.NewGuid(),
            ReadId = readId,
            StartedAt = started,
            EndedAt = started.AddMinutes(minutes),
        });
        await db.SaveChangesAsync();
    }
}
