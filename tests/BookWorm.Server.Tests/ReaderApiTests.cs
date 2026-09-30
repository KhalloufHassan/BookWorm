using System.Net;
using BookWorm.Contracts;
using BookWorm.Server.Data;
using BookWorm.Server.Tests.Infrastructure;
using BookWorm.UI.Api;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BookWorm.Server.Tests;

public sealed class ReaderApiTests(BookWormAppFactory app)
{
    [Fact]
    public async Task OpeningABook_StartsARead_OnlyOnce()
    {
        var api = (await app.CreateUserAsync()).Api;
        var book = await api.AddBookAsync("Moby-Dick");

        var first = await api.StartOrResumeReadAsync(book.Id);
        var second = await api.StartOrResumeReadAsync(book.Id);

        Assert.Equal(ReadStatus.CurrentlyReading, first.Status);
        Assert.NotNull(first.StartedAt);
        Assert.Equal(first.Id, second.Id);
        Assert.Single(await api.GetReadsAsync(book.Id));
        Assert.Equal(BookStatus.WantToRead, (await api.GetBookAsync(book.Id)).Status);
    }

    [Fact]
    public async Task OpeningABook_AfterFinishingIt_StartsANewRead()
    {
        var api = (await app.CreateUserAsync()).Api;
        var book = await api.AddBookAsync("Moby-Dick");
        var first = await api.StartOrResumeReadAsync(book.Id);
        await api.FinishReadAsync(book.Id, first.Id, new FinishReadRequest());

        var second = await api.StartOrResumeReadAsync(book.Id);

        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal(2, (await api.GetReadsAsync(book.Id)).Count);
    }

    [Fact]
    public async Task Progress_IsSavedOnTheRead_AndShowsInContinueReading()
    {
        var api = (await app.CreateUserAsync()).Api;
        var book = await api.AddBookAsync("Moby-Dick");
        var file = await api.UploadSampleAsync(book.Id, BookFormat.Epub);
        var read = await api.StartOrResumeReadAsync(book.Id);

        await api.SaveProgressAsync(book.Id, read.Id, new ReadProgressRequest
        {
            FileId = file.Id,
            Location = "epubcfi(/6/4!/4/2/1:0)",
            Progress = 0.42,
            SessionId = Guid.NewGuid(),
            SessionStartedAt = DateTimeOffset.UtcNow.AddMinutes(-20),
            SessionStartProgress = 0.3,
        });

        var saved = Assert.Single(await api.GetReadsAsync(book.Id));
        Assert.Equal(file.Id, saved.FileId);
        Assert.Equal("epubcfi(/6/4!/4/2/1:0)", saved.Location);
        Assert.Equal(0.42, saved.Progress);
        Assert.NotNull(saved.LastOpenedAt);

        var current = Assert.Single(await api.GetCurrentReadsAsync());
        Assert.Equal(book.Id, current.BookId);
        Assert.Equal(0.42, current.Read.Progress);
        Assert.Equal(0.42, Assert.Single((await api.GetBooksAsync()).Items).Progress);
    }

    [Fact]
    public async Task Progress_ExtendsTheSameSession()
    {
        var user = await app.CreateUserAsync();
        var book = await user.Api.AddBookAsync("Moby-Dick");
        var file = await user.Api.UploadSampleAsync(book.Id, BookFormat.Epub);
        var read = await user.Api.StartOrResumeReadAsync(book.Id);
        var sessionId = Guid.NewGuid();
        var started = DateTimeOffset.UtcNow.AddMinutes(-30);

        foreach (var progress in new[] { 0.1, 0.15, 0.2 })
        {
            await user.Api.SaveProgressAsync(book.Id, read.Id, new ReadProgressRequest
            {
                FileId = file.Id,
                Location = "page",
                Progress = progress,
                SessionId = sessionId,
                SessionStartedAt = started,
                SessionStartProgress = 0.1,
            });
        }

        await using var scope = app.Services.CreateAsyncScope();
        var session = await scope.ServiceProvider.GetRequiredService<AppDbContext>().ReadingSessions
            .IgnoreQueryFilters().SingleAsync(s => s.Id == sessionId);
        Assert.Equal(read.Id, session.ReadId);
        Assert.Equal(0.1, session.StartProgress);
        Assert.Equal(0.2, session.EndProgress);
        Assert.InRange((session.EndedAt - session.StartedAt).TotalMinutes, 29, 31);
    }

    [Fact]
    public async Task FinishingFromTheReader_CanAlsoUpdateTheBook()
    {
        var api = (await app.CreateUserAsync()).Api;
        var book = await api.AddBookAsync("Moby-Dick", status: BookStatus.CurrentlyReading);
        var read = await api.StartOrResumeReadAsync(book.Id);

        var finished = await api.FinishReadAsync(book.Id, read.Id, new FinishReadRequest { UpdateBookStatus = true });

        Assert.Equal(ReadStatus.Finished, finished.Status);
        Assert.NotNull(finished.FinishedAt);
        Assert.Equal(BookStatus.Finished, (await api.GetBookAsync(book.Id)).Status);
        Assert.Empty(await api.GetCurrentReadsAsync());
    }

    [Fact]
    public async Task Progress_MustReferToAFileOfTheBook()
    {
        var api = (await app.CreateUserAsync()).Api;
        var book = await api.AddBookAsync("Moby-Dick");
        var other = await api.AddBookAsync("Typee");
        var otherFile = await api.UploadSampleAsync(other.Id, BookFormat.Pdf);
        var read = await api.StartOrResumeReadAsync(book.Id);

        var error = await Assert.ThrowsAsync<ApiException>(() => api.SaveProgressAsync(book.Id, read.Id,
            new ReadProgressRequest { FileId = otherFile.Id, Location = "page:1", Progress = 0.1 }));

        Assert.Equal(HttpStatusCode.BadRequest, error.StatusCode);
    }

    [Fact]
    public async Task OtherUsers_CannotSaveProgressOnYourReads()
    {
        var owner = (await app.CreateUserAsync()).Api;
        var book = await owner.AddBookAsync("Moby-Dick");
        var file = await owner.UploadSampleAsync(book.Id, BookFormat.Epub);
        var read = await owner.StartOrResumeReadAsync(book.Id);
        var stranger = (await app.CreateUserAsync()).Api;

        var progress = await Assert.ThrowsAsync<ApiException>(() => stranger.SaveProgressAsync(book.Id, read.Id,
            new ReadProgressRequest { FileId = file.Id, Location = "x", Progress = 0.9 }));
        var start = await Assert.ThrowsAsync<ApiException>(() => stranger.StartOrResumeReadAsync(book.Id));

        Assert.Equal(HttpStatusCode.BadRequest, progress.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, start.StatusCode);
        Assert.Null(Assert.Single(await owner.GetReadsAsync(book.Id)).Progress);
    }

    [Fact]
    public async Task MarkingTheBookFinished_FinishesTheOngoingRead()
    {
        var api = (await app.CreateUserAsync()).Api;
        var book = await api.AddBookAsync("Moby-Dick");
        var read = await api.StartOrResumeReadAsync(book.Id);

        var update = (await api.GetBookAsync(book.Id)).ToUpdate();
        update.Status = BookStatus.Finished;
        var updated = await api.UpdateBookAsync(book.Id, update);

        var finished = Assert.Single(updated.Reads);
        Assert.Equal(read.Id, finished.Id);
        Assert.Equal(ReadStatus.Finished, finished.Status);
        Assert.NotNull(finished.FinishedAt);
    }

    [Fact]
    public async Task EditingAnAlreadyFinishedBook_LeavesARereadGoing()
    {
        var api = (await app.CreateUserAsync()).Api;
        var book = await api.AddBookAsync("Moby-Dick");
        var first = await api.StartOrResumeReadAsync(book.Id);
        await api.FinishReadAsync(book.Id, first.Id, new FinishReadRequest { UpdateBookStatus = true });
        var reread = await api.StartOrResumeReadAsync(book.Id);

        var update = (await api.GetBookAsync(book.Id)).ToUpdate();
        update.Rating = 5;
        var updated = await api.UpdateBookAsync(book.Id, update);

        Assert.Equal(ReadStatus.CurrentlyReading, updated.Reads.Single(r => r.Id == reread.Id).Status);
    }

    [Fact]
    public async Task OpeningABook_WithNoReadInProgress_OnlyBrowses()
    {
        var api = (await app.CreateUserAsync()).Api;
        var book = await api.AddBookAsync("Moby-Dick");

        Assert.Null(await api.GetCurrentReadAsync(book.Id));

        var started = await api.StartOrResumeReadAsync(book.Id);
        Assert.Equal(started.Id, (await api.GetCurrentReadAsync(book.Id))?.Id);
    }

    [Fact]
    public async Task Browsing_RemembersThePosition_WithoutRecordingARead()
    {
        var api = (await app.CreateUserAsync()).Api;
        var book = await api.AddBookAsync("Moby-Dick");
        var file = await api.UploadSampleAsync(book.Id, BookFormat.Epub);

        await api.SaveBrowsePositionAsync(book.Id, file.Id, new BrowsePositionRequest { Location = "epubcfi(/6/2!/4/2)", Progress = 0.4 });

        var saved = Assert.Single((await api.GetBookAsync(book.Id)).Files);
        Assert.Equal("epubcfi(/6/2!/4/2)", saved.BrowseLocation);
        Assert.Equal(0.4, saved.BrowseProgress);
        Assert.Empty(await api.GetReadsAsync(book.Id));
    }

    [Fact]
    public async Task OtherUsers_CannotSeeOrSaveYourBrowsing()
    {
        var owner = (await app.CreateUserAsync()).Api;
        var book = await owner.AddBookAsync("Moby-Dick");
        var file = await owner.UploadSampleAsync(book.Id, BookFormat.Epub);
        var stranger = (await app.CreateUserAsync()).Api;

        var save = await Assert.ThrowsAsync<ApiException>(() => stranger.SaveBrowsePositionAsync(book.Id, file.Id,
            new BrowsePositionRequest { Location = "x", Progress = 0.9 }));
        var current = await Assert.ThrowsAsync<ApiException>(() => stranger.GetCurrentReadAsync(book.Id));

        Assert.Equal(HttpStatusCode.NotFound, save.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, current.StatusCode);
        Assert.Null(Assert.Single((await owner.GetBookAsync(book.Id)).Files).BrowseLocation);
    }
}
