using System.Net;
using BookWorm.Contracts;
using BookWorm.Server.Tests.Infrastructure;
using BookWorm.UI.Api;

namespace BookWorm.Server.Tests;

public sealed class AuthorsApiTests(BookWormAppFactory app)
{
    [Fact]
    public async Task Create_RejectsDeathBeforeBirth()
    {
        var api = (await app.CreateUserAsync()).Api;

        var error = await Assert.ThrowsAsync<ApiException>(() =>
            api.AddAuthorAsync("Time Traveller", born: new DateOnly(1900, 1, 1), died: new DateOnly(1899, 1, 1)));

        Assert.Equal(HttpStatusCode.BadRequest, error.StatusCode);
        Assert.Contains("deathDate", error.Errors.Keys);
    }

    [Fact]
    public async Task Get_ListsTheirBooks()
    {
        var api = (await app.CreateUserAsync()).Api;
        var austen = await api.AddAuthorAsync("Jane Austen", new DateOnly(1775, 12, 16), new DateOnly(1817, 7, 18));
        await api.AddBookAsync("Persuasion", [austen.Id], rating: 5.0m);
        await api.AddBookAsync("Emma", [austen.Id]);

        var details = await api.GetAuthorAsync(austen.Id);

        Assert.Equal(new DateOnly(1775, 12, 16), details.BirthDate);
        Assert.Equal(new DateOnly(1817, 7, 18), details.DeathDate);
        Assert.Equal(["Emma", "Persuasion"], details.Books.Select(b => b.Title));
    }

    [Fact]
    public async Task Update_ChangesDetailsAndChecksTheVersion()
    {
        var api = (await app.CreateUserAsync()).Api;
        var author = await api.AddAuthorAsync("Mary Shelly");

        var fixedName = await api.UpdateAuthorAsync(author.Id, new UpdateAuthorRequest
        {
            Name = "Mary Shelley",
            BirthDate = new DateOnly(1797, 8, 30),
            Version = author.Version,
        });
        var stale = await Assert.ThrowsAsync<ApiException>(() =>
            api.UpdateAuthorAsync(author.Id, new UpdateAuthorRequest { Name = "M. Shelley", Version = author.Version }));

        Assert.Equal("Mary Shelley", fixedName.Name);
        Assert.Equal(new DateOnly(1797, 8, 30), fixedName.BirthDate);
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
    }

    [Fact]
    public async Task Delete_KeepsTheirBooks()
    {
        var api = (await app.CreateUserAsync()).Api;
        var author = await api.AddAuthorAsync("Soon Removed");
        var book = await api.AddBookAsync("Orphaned but kept", [author.Id]);

        await api.DeleteAuthorAsync(author.Id);

        Assert.Empty((await api.GetBookAsync(book.Id)).Authors);
    }

    [Fact]
    public async Task List_FiltersByDatesAndSortsByBookCount()
    {
        var api = (await app.CreateUserAsync()).Api;
        var woolf = await api.AddAuthorAsync("Virginia Woolf", new DateOnly(1882, 1, 25), new DateOnly(1941, 3, 28));
        var atwood = await api.AddAuthorAsync("Margaret Atwood", new DateOnly(1939, 11, 18));
        await api.AddBookAsync("The Handmaid's Tale", [atwood.Id]);
        await api.AddBookAsync("Oryx and Crake", [atwood.Id]);
        await api.AddBookAsync("Orlando", [woolf.Id]);

        var bornBefore1900 = await api.GetAuthorsAsync(new AuthorListQuery { BornTo = new DateOnly(1900, 1, 1) });
        var byBooks = await api.GetAuthorsAsync(new AuthorListQuery { Sort = AuthorSort.BookCount });

        Assert.Equal(["Virginia Woolf"], bornBefore1900.Items.Select(a => a.Name));
        Assert.Equal(["Margaret Atwood", "Virginia Woolf"], byBooks.Items.Select(a => a.Name));
        Assert.Equal(2, byBooks.Items[0].BookCount);
    }
}
