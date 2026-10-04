using System.Net;
using BookWorm.Contracts;
using BookWorm.Server.Tests.Infrastructure;
using BookWorm.UI.Api;

namespace BookWorm.Server.Tests;

public sealed class CollectionsApiTests(BookWormAppFactory app)
{
    [Fact]
    public async Task Names_AreUniquePerUserIgnoringCase()
    {
        var alice = (await app.CreateUserAsync()).Api;
        var bob = (await app.CreateUserAsync()).Api;
        await alice.AddCollectionAsync("Discworld");

        var duplicate = await Assert.ThrowsAsync<ApiException>(() => alice.AddCollectionAsync("discworld"));
        var bobsCollection = await bob.AddCollectionAsync("Discworld");

        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        Assert.Equal("Discworld", bobsCollection.Name);
    }

    [Fact]
    public async Task Update_ChangesNameAndType_AndRejectsAStaleVersion()
    {
        var api = (await app.CreateUserAsync()).Api;
        var collection = await api.AddCollectionAsync("Britannica");

        var updated = await api.UpdateCollectionAsync(collection.Id,
            new UpdateCollectionRequest { Name = "Encyclopaedia Britannica", Type = CollectionType.Volumes, Version = collection.Version });
        var stale = await Assert.ThrowsAsync<ApiException>(() => api.UpdateCollectionAsync(collection.Id,
            new UpdateCollectionRequest { Name = "Other", Type = CollectionType.Related, Version = collection.Version }));

        Assert.Equal("Encyclopaedia Britannica", updated.Name);
        Assert.Equal(CollectionType.Volumes, updated.Type);
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
    }

    [Fact]
    public async Task SetBooks_KeepsTheGivenOrder_AndCanReorderAndRemove()
    {
        var api = (await app.CreateUserAsync()).Api;
        var first = await api.AddBookAsync("The Fellowship of the Ring");
        var second = await api.AddBookAsync("The Two Towers");
        var third = await api.AddBookAsync("The Return of the King");
        var collection = await api.AddCollectionAsync("The Lord of the Rings");

        var set = await api.SetCollectionBooksAsync(collection.Id, [third.Id, first.Id, second.Id]);
        var reordered = await api.SetCollectionBooksAsync(collection.Id, [first.Id, second.Id, third.Id]);
        var removed = await api.SetCollectionBooksAsync(collection.Id, [first.Id, third.Id]);

        Assert.Equal([third.Id, first.Id, second.Id], set.Books.Select(b => b.Id));
        Assert.Equal([first.Id, second.Id, third.Id], reordered.Books.Select(b => b.Id));
        Assert.Equal([first.Id, third.Id], removed.Books.Select(b => b.Id));
        Assert.Equal([first.Id, third.Id], (await api.GetCollectionAsync(collection.Id)).Books.Select(b => b.Id));
        Assert.Equal(2, (await api.GetCollectionsAsync()).Single().BookCount);
        Assert.Equal(3, (await api.GetBooksAsync()).TotalCount);
    }

    [Fact]
    public async Task SetBooks_ChangesTheVersion()
    {
        var api = (await app.CreateUserAsync()).Api;
        var book = await api.AddBookAsync("Mort");
        var collection = await api.AddCollectionAsync("Death");

        var withBook = await api.SetCollectionBooksAsync(collection.Id, [book.Id]);

        Assert.NotEqual(collection.Version, withBook.Version);
    }

    [Fact]
    public async Task ABook_CanBeInSeveralCollections()
    {
        var api = (await app.CreateUserAsync()).Api;
        var book = await api.AddBookAsync("Guards! Guards!");
        var discworld = await api.AddCollectionAsync("Discworld");
        var cityWatch = await api.AddCollectionAsync("City Watch");

        await api.SetCollectionBooksAsync(discworld.Id, [book.Id]);
        await api.SetCollectionBooksAsync(cityWatch.Id, [book.Id]);

        Assert.All(await api.GetCollectionsAsync(), c => Assert.Equal(1, c.BookCount));
    }

    [Fact]
    public async Task SetBooks_RejectsDuplicatesAndUnknownBooks()
    {
        var api = (await app.CreateUserAsync()).Api;
        var book = await api.AddBookAsync("Dune");
        var collection = await api.AddCollectionAsync("Dune");

        var duplicate = await Assert.ThrowsAsync<ApiException>(() => api.SetCollectionBooksAsync(collection.Id, [book.Id, book.Id]));
        var unknown = await Assert.ThrowsAsync<ApiException>(() => api.SetCollectionBooksAsync(collection.Id, [Guid.NewGuid()]));

        Assert.Equal(HttpStatusCode.BadRequest, duplicate.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
    }

    [Fact]
    public async Task DeletingABook_RemovesItFromCollections_AndDeletingACollectionKeepsTheBooks()
    {
        var api = (await app.CreateUserAsync()).Api;
        var kept = await api.AddBookAsync("Kept");
        var deleted = await api.AddBookAsync("Deleted");
        var collection = await api.AddCollectionAsync("Mixed", CollectionType.Related);
        await api.SetCollectionBooksAsync(collection.Id, [deleted.Id, kept.Id]);

        await api.DeleteBookAsync(deleted.Id);
        var afterBookDelete = await api.GetCollectionAsync(collection.Id);
        await api.DeleteCollectionAsync(collection.Id);

        Assert.Equal([kept.Id], afterBookDelete.Books.Select(b => b.Id));
        Assert.Empty(await api.GetCollectionsAsync());
        Assert.Equal("Kept", (await api.GetBookAsync(kept.Id)).Title);
    }

    [Fact]
    public async Task Collections_ArePrivate()
    {
        var alice = (await app.CreateUserAsync()).Api;
        var bob = (await app.CreateUserAsync()).Api;
        var aliceBook = await alice.AddBookAsync("Alice's book");
        var collection = await alice.AddCollectionAsync("Alice's collection");
        var bobsCollection = await bob.AddCollectionAsync("Bob's collection");

        Assert.Single(await bob.GetCollectionsAsync());
        await AssertStatus(HttpStatusCode.NotFound, () => bob.GetCollectionAsync(collection.Id));
        await AssertStatus(HttpStatusCode.NotFound, () => bob.SetCollectionBooksAsync(collection.Id, []));
        await AssertStatus(HttpStatusCode.NotFound, () => bob.DeleteCollectionAsync(collection.Id));
        await AssertStatus(HttpStatusCode.BadRequest, () => bob.SetCollectionBooksAsync(bobsCollection.Id, [aliceBook.Id]));
    }

    [Fact]
    public async Task BookEdits_SetCollections_AddingAtTheEnd()
    {
        var api = (await app.CreateUserAsync()).Api;
        var first = await api.AddBookAsync("Mort");
        var second = await api.AddBookAsync("Reaper Man");
        var discworld = await api.AddCollectionAsync("Discworld");
        var death = await api.AddCollectionAsync("Death");
        await api.SetCollectionBooksAsync(discworld.Id, [first.Id]);

        var created = await api.CreateBookAsync(new CreateBookRequest { Title = "Soul Music", CollectionIds = [death.Id, discworld.Id] });
        var update = second.ToUpdate();
        update.CollectionIds = [discworld.Id];
        var updated = await api.UpdateBookAsync(second.Id, update);

        Assert.Equal(["Death", "Discworld"], created.Collections.Select(c => c.Name));
        Assert.Equal(["Discworld"], updated.Collections.Select(c => c.Name));
        Assert.Equal([first.Id, created.Id, second.Id], (await api.GetCollectionAsync(discworld.Id)).Books.Select(b => b.Id));

        var removed = updated.ToUpdate();
        removed.CollectionIds = [];
        await api.UpdateBookAsync(second.Id, removed);
        Assert.Equal([first.Id, created.Id], (await api.GetCollectionAsync(discworld.Id)).Books.Select(b => b.Id));
    }

    [Fact]
    public async Task BookEdits_WithoutCollectionIds_LeaveCollectionsAlone()
    {
        var api = (await app.CreateUserAsync()).Api;
        var book = await api.AddBookAsync("Dune");
        var collection = await api.AddCollectionAsync("Dune");
        await api.SetCollectionBooksAsync(collection.Id, [book.Id]);

        var update = (await api.GetBookAsync(book.Id)).ToUpdate();
        update.CollectionIds = null;
        update.Title = "Dune (1965)";
        var updated = await api.UpdateBookAsync(book.Id, update);

        Assert.Equal(["Dune"], updated.Collections.Select(c => c.Name));
    }

    [Fact]
    public async Task BookEdits_RejectSomeoneElsesCollections()
    {
        var alice = (await app.CreateUserAsync()).Api;
        var bob = (await app.CreateUserAsync()).Api;
        var aliceCollection = await alice.AddCollectionAsync("Alice's");

        await AssertStatus(HttpStatusCode.BadRequest,
            () => bob.CreateBookAsync(new CreateBookRequest { Title = "Bob's", CollectionIds = [aliceCollection.Id] }));
    }

    private static async Task AssertStatus(HttpStatusCode expected, Func<Task> action)
    {
        var error = await Assert.ThrowsAsync<ApiException>(action);
        Assert.Equal(expected, error.StatusCode);
    }
}
