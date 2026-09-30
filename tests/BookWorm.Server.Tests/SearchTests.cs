using BookWorm.Contracts;
using BookWorm.Server.Tests.Infrastructure;
using BookWorm.UI.Api;

namespace BookWorm.Server.Tests;

public sealed class SearchTests(BookWormAppFactory app)
{
    [Theory]
    [InlineData("garcia marquez")]
    [InlineData("GARCÍA")]
    [InlineData("anos")]
    [InlineData("soledad")]
    public async Task Books_IgnoreCaseAndAccents(string search)
    {
        var api = (await app.CreateUserAsync()).Api;
        var author = await api.AddAuthorAsync("Gabriel García Márquez");
        await api.AddBookAsync("Cien años de soledad", [author.Id]);
        await api.AddBookAsync("Unrelated");

        await AssertFinds(api, search, "Cien años de soledad");
    }

    [Theory]
    [InlineData("tolkin")]
    [InlineData("hobit")]
    [InlineData("the hobbit")]
    public async Task Books_TolerateTyposInTitlesAndAuthors(string search)
    {
        var api = (await app.CreateUserAsync()).Api;
        var tolkien = await api.AddAuthorAsync("J.R.R. Tolkien");
        await api.AddBookAsync("The Hobbit", [tolkien.Id]);
        await api.AddBookAsync("Dune");

        await AssertFinds(api, search, "The Hobbit");
    }

    [Theory]
    [InlineData("empire", "Vampire Academy")]
    [InlineData("rings", "Six of Kings")]
    [InlineData("1984", "1985")]
    public async Task Books_DoNotMatchLookAlikeWordsOrNumbers(string search, string lookAlike)
    {
        var api = (await app.CreateUserAsync()).Api;
        await api.AddBookAsync(lookAlike);

        await AssertFinds(api, search);
    }

    [Fact]
    public async Task Books_MatchTagsAndNotes()
    {
        var api = (await app.CreateUserAsync()).Api;
        var cozy = await api.AddTagAsync("Cozy mystery");
        await api.AddBookAsync("The Thursday Murder Club", tagIds: [cozy.Id]);
        await api.AddBookAsync("Station Eleven", notes: "Finished it on a long train ride.");

        await AssertFinds(api, "mystery", "The Thursday Murder Club");
        await AssertFinds(api, "train", "Station Eleven");
    }

    [Fact]
    public async Task Books_RequireEveryWordToMatch()
    {
        var api = (await app.CreateUserAsync()).Api;
        var tolkien = await api.AddAuthorAsync("J.R.R. Tolkien");
        await api.AddBookAsync("The Hobbit", [tolkien.Id]);

        await AssertFinds(api, "hobbit tolkien", "The Hobbit");
        await AssertFinds(api, "hobbit asimov");
    }

    [Fact]
    public async Task Books_TreatLikeWildcardsAsText()
    {
        var api = (await app.CreateUserAsync()).Api;
        await api.AddBookAsync("100% Wolf");
        await api.AddBookAsync("1000 Years");

        await AssertFinds(api, "100%", "100% Wolf");
        await AssertFinds(api, "_");
    }

    [Fact]
    public async Task Books_AreRankedByRelevance()
    {
        var api = (await app.CreateUserAsync()).Api;
        await api.AddBookAsync("A Memory Called Empire");
        await api.AddBookAsync("Empire");

        var result = await api.GetBooksAsync(new BookListQuery { Search = "empire" });

        Assert.Equal("Empire", result.Items[0].Title);
    }

    [Theory]
    [InlineData("marquez")]
    [InlineData("gabriel garcia")]
    [InlineData("garsia")]
    public async Task Authors_IgnoreAccentsAndTolerateTypos(string search)
    {
        var api = (await app.CreateUserAsync()).Api;
        await api.AddAuthorAsync("Gabriel García Márquez");
        await api.AddAuthorAsync("Mary Shelley");

        var result = await api.GetAuthorsAsync(new AuthorListQuery { Search = search });

        Assert.Equal(["Gabriel García Márquez"], result.Items.Select(a => a.Name));
    }

    private static async Task AssertFinds(BookWormApiClient api, string search, params string[] titles)
    {
        var result = await api.GetBooksAsync(new BookListQuery { Search = search });
        Assert.Equal(titles.Order(), result.Items.Select(b => b.Title).Order());
    }
}
