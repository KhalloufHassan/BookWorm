using System.Net;
using BookWorm.Contracts;
using BookWorm.Server.Tests.Infrastructure;
using BookWorm.UI.Api;

namespace BookWorm.Server.Tests;

public sealed class BooksApiTests(BookWormAppFactory app)
{
    [Fact]
    public async Task CreateAndGet_RoundTripsEveryField()
    {
        var api = (await app.CreateUserAsync()).Api;
        var pratchett = await api.AddAuthorAsync("Terry Pratchett");
        var gaiman = await api.AddAuthorAsync("Neil Gaiman");
        var fantasy = await api.AddTagAsync("Fantasy");

        var created = await api.AddBookAsync(
            "Good Omens",
            authorIds: [gaiman.Id, pratchett.Id],
            tagIds: [fantasy.Id],
            status: BookStatus.Finished,
            rating: 4.8m,
            published: new DateOnly(1990, 5, 1),
            notes: "# Thoughts\nFunny *and* kind.");

        var book = await api.GetBookAsync(created.Id);

        Assert.Equal("Good Omens", book.Title);
        Assert.Equal(BookStatus.Finished, book.Status);
        Assert.Equal(4.8m, book.Rating);
        Assert.Equal(new DateOnly(1990, 5, 1), book.OriginalPublicationDate);
        Assert.Equal("# Thoughts\nFunny *and* kind.", book.Notes);
        Assert.Equal(["Neil Gaiman", "Terry Pratchett"], book.Authors.Select(a => a.Name));
        Assert.Equal(["Fantasy"], book.Tags.Select(t => t.Name));
        Assert.Empty(book.Reads);
        Assert.NotEqual(0u, book.Version);
        Assert.Equal(7, book.Id.Version);
    }

    [Fact]
    public async Task Create_DefaultsToWantToRead()
    {
        var api = (await app.CreateUserAsync()).Api;

        var book = await api.AddBookAsync("Piranesi");

        Assert.Equal(BookStatus.WantToRead, book.Status);
        Assert.Null(book.Rating);
    }

    [Theory]
    [InlineData("5.1")]
    [InlineData("-0.5")]
    [InlineData("4.85")]
    public async Task Create_RejectsRatingsOutsideZeroToFiveWithOneDecimal(string rating)
    {
        var api = (await app.CreateUserAsync()).Api;

        var error = await Assert.ThrowsAsync<ApiException>(() =>
            api.AddBookAsync("Dune", rating: decimal.Parse(rating, System.Globalization.CultureInfo.InvariantCulture)));

        Assert.Equal(HttpStatusCode.BadRequest, error.StatusCode);
        Assert.Contains("rating", error.Errors.Keys);
    }

    [Fact]
    public async Task Create_RequiresATitle()
    {
        var api = (await app.CreateUserAsync()).Api;

        var error = await Assert.ThrowsAsync<ApiException>(() => api.AddBookAsync("   "));

        Assert.Equal(HttpStatusCode.BadRequest, error.StatusCode);
        Assert.Contains("title", error.Errors.Keys);
    }

    [Fact]
    public async Task Update_ReplacesDetailsAuthorsAndTags()
    {
        var api = (await app.CreateUserAsync()).Api;
        var first = await api.AddAuthorAsync("First Author");
        var second = await api.AddAuthorAsync("Second Author");
        var tag = await api.AddTagAsync("Classics");
        var book = await api.AddBookAsync("Draft title", [first.Id], [tag.Id]);

        var update = book.ToUpdate();
        update.Title = "Final title";
        update.Status = BookStatus.CurrentlyReading;
        update.Rating = 3.5m;
        update.AuthorIds = [second.Id, first.Id];
        update.TagIds = [];
        var updated = await api.UpdateBookAsync(book.Id, update);

        Assert.Equal("Final title", updated.Title);
        Assert.Equal(BookStatus.CurrentlyReading, updated.Status);
        Assert.Equal(3.5m, updated.Rating);
        Assert.Equal(["Second Author", "First Author"], updated.Authors.Select(a => a.Name));
        Assert.Empty(updated.Tags);
        Assert.NotEqual(book.Version, updated.Version);
        Assert.True(updated.UpdatedAt > book.UpdatedAt);
    }

    [Fact]
    public async Task Update_WithAStaleVersion_IsRejected()
    {
        var api = (await app.CreateUserAsync()).Api;
        var book = await api.AddBookAsync("Contested");

        var fromPhone = book.ToUpdate();
        fromPhone.Title = "Edited on the phone";
        await api.UpdateBookAsync(book.Id, fromPhone);

        var fromWeb = book.ToUpdate();
        fromWeb.Title = "Edited on the web";
        var error = await Assert.ThrowsAsync<ApiException>(() => api.UpdateBookAsync(book.Id, fromWeb));

        Assert.Equal(HttpStatusCode.Conflict, error.StatusCode);
        Assert.Equal("Edited on the phone", (await api.GetBookAsync(book.Id)).Title);
    }

    [Fact]
    public async Task Update_OnlyChangingTags_StillChecksTheVersion()
    {
        var api = (await app.CreateUserAsync()).Api;
        var tag = await api.AddTagAsync("Favourites");
        var book = await api.AddBookAsync("Tagged twice");

        var first = book.ToUpdate();
        first.TagIds = [tag.Id];
        await api.UpdateBookAsync(book.Id, first);

        var second = book.ToUpdate();
        second.TagIds = [];
        var error = await Assert.ThrowsAsync<ApiException>(() => api.UpdateBookAsync(book.Id, second));

        Assert.Equal(HttpStatusCode.Conflict, error.StatusCode);
    }

    [Fact]
    public async Task Delete_RemovesTheBookAndItsReads()
    {
        var api = (await app.CreateUserAsync()).Api;
        var book = await api.AddBookAsync("Gone Girl");
        await api.CreateReadAsync(book.Id, new CreateReadRequest { Status = ReadStatus.CurrentlyReading });

        await api.DeleteBookAsync(book.Id);

        var error = await Assert.ThrowsAsync<ApiException>(() => api.GetBookAsync(book.Id));
        Assert.Equal(HttpStatusCode.NotFound, error.StatusCode);
        var readsError = await Assert.ThrowsAsync<ApiException>(() => api.GetReadsAsync(book.Id));
        Assert.Equal(HttpStatusCode.NotFound, readsError.StatusCode);
    }

    [Fact]
    public async Task List_FiltersByStatusRatingDateTagsAndAuthors()
    {
        var api = (await app.CreateUserAsync()).Api;
        var le = await api.AddAuthorAsync("Ursula K. Le Guin");
        var herbert = await api.AddAuthorAsync("Frank Herbert");
        var sciFi = await api.AddTagAsync("Sci-fi");
        var classic = await api.AddTagAsync("Classic");

        var dispossessed = await api.AddBookAsync("The Dispossessed", [le.Id], [sciFi.Id, classic.Id], BookStatus.Finished, 4.5m, new DateOnly(1974, 5, 1));
        var earthsea = await api.AddBookAsync("A Wizard of Earthsea", [le.Id], [classic.Id], BookStatus.WantToRead, null, new DateOnly(1968, 11, 1));
        var dune = await api.AddBookAsync("Dune", [herbert.Id], [sciFi.Id], BookStatus.Skipped, 3.0m, new DateOnly(1965, 8, 1));

        await AssertTitles(api, new BookListQuery { Status = [BookStatus.Finished, BookStatus.Skipped] }, dispossessed, dune);
        await AssertTitles(api, new BookListQuery { RatingMin = 4m }, dispossessed);
        await AssertTitles(api, new BookListQuery { RatingMax = 4m }, dune);
        await AssertTitles(api, new BookListQuery { PublishedFrom = new DateOnly(1966, 1, 1), PublishedTo = new DateOnly(1970, 1, 1) }, earthsea);
        await AssertTitles(api, new BookListQuery { TagIds = [sciFi.Id] }, dispossessed, dune);
        await AssertTitles(api, new BookListQuery { TagIds = [sciFi.Id, classic.Id] }, dispossessed, earthsea, dune);
        await AssertTitles(api, new BookListQuery { AuthorIds = [herbert.Id] }, dune);
    }

    [Fact]
    public async Task List_SortsAndPages()
    {
        var api = (await app.CreateUserAsync()).Api;
        await api.AddBookAsync("B unrated");
        await api.AddBookAsync("C rated low", rating: 2.0m);
        await api.AddBookAsync("A rated high", rating: 4.9m);

        var byTitle = await api.GetBooksAsync();
        Assert.Equal(["A rated high", "B unrated", "C rated low"], byTitle.Items.Select(b => b.Title));

        // Best first by default, unrated last in both directions.
        var byRating = await api.GetBooksAsync(new BookListQuery { Sort = BookSort.Rating });
        Assert.Equal(["A rated high", "C rated low", "B unrated"], byRating.Items.Select(b => b.Title));
        var byRatingAscending = await api.GetBooksAsync(new BookListQuery { Sort = BookSort.Rating, Direction = SortDirection.Asc });
        Assert.Equal(["C rated low", "A rated high", "B unrated"], byRatingAscending.Items.Select(b => b.Title));

        var secondPage = await api.GetBooksAsync(new BookListQuery { Page = 2, PageSize = 2 });
        Assert.Equal(3, secondPage.TotalCount);
        Assert.Equal(["C rated low"], secondPage.Items.Select(b => b.Title));
    }

    [Fact]
    public async Task List_SortsByFirstAuthor()
    {
        var api = (await app.CreateUserAsync()).Api;
        var zadie = await api.AddAuthorAsync("Zadie Smith");
        var anne = await api.AddAuthorAsync("Anne Carson");
        await api.AddBookAsync("White Teeth", [zadie.Id]);
        await api.AddBookAsync("Autobiography of Red", [anne.Id, zadie.Id]);
        await api.AddBookAsync("Anonymous");

        var result = await api.GetBooksAsync(new BookListQuery { Sort = BookSort.Author });

        Assert.Equal(["Autobiography of Red", "White Teeth", "Anonymous"], result.Items.Select(b => b.Title));
    }

    private static async Task AssertTitles(BookWormApiClient api, BookListQuery query, params BookDetails[] expected)
    {
        var result = await api.GetBooksAsync(query);
        Assert.Equal(expected.Select(b => b.Title).Order(), result.Items.Select(b => b.Title).Order());
        Assert.Equal(expected.Length, result.TotalCount);
    }
}
