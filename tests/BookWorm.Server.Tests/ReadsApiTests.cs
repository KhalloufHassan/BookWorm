using System.Net;
using BookWorm.Contracts;
using BookWorm.Server.Tests.Infrastructure;
using BookWorm.UI.Api;

namespace BookWorm.Server.Tests;

public sealed class ReadsApiTests(BookWormAppFactory app)
{
    // Whole seconds: PostgreSQL stores microseconds, .NET ticks are finer.
    private static readonly DateTimeOffset Started = new(2026, 3, 1, 21, 30, 0, TimeSpan.FromHours(2));

    [Fact]
    public async Task Reads_CanBeRecordedUpdatedAndDeleted()
    {
        var api = (await app.CreateUserAsync()).Api;
        var book = await api.AddBookAsync("Middlemarch");

        var read = await api.CreateReadAsync(book.Id, new CreateReadRequest { StartedAt = Started });
        Assert.Equal(ReadStatus.CurrentlyReading, read.Status);
        Assert.Equal(Started, read.StartedAt);
        Assert.Equal(TimeSpan.Zero, read.StartedAt.Value.Offset);

        var finished = await api.UpdateReadAsync(book.Id, read.Id, new UpdateReadRequest
        {
            Status = ReadStatus.Finished,
            StartedAt = Started,
            FinishedAt = Started.AddDays(12),
            Version = read.Version,
        });
        Assert.Equal(ReadStatus.Finished, finished.Status);
        Assert.Equal(Started.AddDays(12), finished.FinishedAt);

        await api.DeleteReadAsync(book.Id, read.Id);
        Assert.Empty(await api.GetReadsAsync(book.Id));
    }

    [Fact]
    public async Task ABook_CanBeSkippedAndReadAgainLater()
    {
        var api = (await app.CreateUserAsync()).Api;
        var book = await api.AddBookAsync("Ulysses");

        await api.CreateReadAsync(book.Id, new CreateReadRequest
        {
            Status = ReadStatus.Skipped,
            StartedAt = Started.AddYears(-3),
            FinishedAt = Started.AddYears(-3).AddDays(20),
        });
        await api.CreateReadAsync(book.Id, new CreateReadRequest { Status = ReadStatus.CurrentlyReading, StartedAt = Started });

        var reads = (await api.GetBookAsync(book.Id)).Reads;

        Assert.Equal([ReadStatus.CurrentlyReading, ReadStatus.Skipped], reads.Select(r => r.Status));
    }

    [Fact]
    public async Task AReadInProgress_CannotHaveAFinishTime()
    {
        var api = (await app.CreateUserAsync()).Api;
        var book = await api.AddBookAsync("Moby-Dick");

        var error = await Assert.ThrowsAsync<ApiException>(() => api.CreateReadAsync(book.Id, new CreateReadRequest
        {
            Status = ReadStatus.CurrentlyReading,
            FinishedAt = Started,
        }));

        Assert.Equal(HttpStatusCode.BadRequest, error.StatusCode);
        Assert.Contains("finishedAt", error.Errors.Keys);
    }

    [Fact]
    public async Task TheFinishTime_CannotBeBeforeTheStartTime()
    {
        var api = (await app.CreateUserAsync()).Api;
        var book = await api.AddBookAsync("Moby-Dick");

        var error = await Assert.ThrowsAsync<ApiException>(() => api.CreateReadAsync(book.Id, new CreateReadRequest
        {
            Status = ReadStatus.Finished,
            StartedAt = Started,
            FinishedAt = Started.AddMinutes(-1),
        }));

        Assert.Equal(HttpStatusCode.BadRequest, error.StatusCode);
        Assert.Contains("finishedAt", error.Errors.Keys);
    }

    [Fact]
    public async Task Update_WithAStaleVersion_IsRejected()
    {
        var api = (await app.CreateUserAsync()).Api;
        var book = await api.AddBookAsync("Beloved");
        var read = await api.CreateReadAsync(book.Id, new CreateReadRequest { StartedAt = Started });
        await api.UpdateReadAsync(book.Id, read.Id, new UpdateReadRequest { StartedAt = Started.AddHours(1), Version = read.Version });

        var error = await Assert.ThrowsAsync<ApiException>(() =>
            api.UpdateReadAsync(book.Id, read.Id, new UpdateReadRequest { StartedAt = Started.AddHours(2), Version = read.Version }));

        Assert.Equal(HttpStatusCode.Conflict, error.StatusCode);
    }
}
