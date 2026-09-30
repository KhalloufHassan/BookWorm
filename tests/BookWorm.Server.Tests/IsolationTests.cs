using System.Net;
using BookWorm.Contracts;
using BookWorm.Server.Tests.Infrastructure;
using BookWorm.UI.Api;

namespace BookWorm.Server.Tests;

/// <summary>Libraries are private: nobody can see or touch another user's books, authors, tags or reads.</summary>
public sealed class IsolationTests(BookWormAppFactory app)
{
    [Fact]
    public async Task Users_OnlySeeTheirOwnLibrary()
    {
        var alice = (await app.CreateUserAsync()).Api;
        var bob = (await app.CreateUserAsync()).Api;
        var author = await alice.AddAuthorAsync("Alice's author");
        var tag = await alice.AddTagAsync("Alice's tag");
        await alice.AddBookAsync("Alice's book", [author.Id], [tag.Id]);

        Assert.Equal(0, (await bob.GetBooksAsync()).TotalCount);
        Assert.Equal(0, (await bob.GetAuthorsAsync()).TotalCount);
        Assert.Empty(await bob.GetTagsAsync());
        Assert.Equal(0, (await bob.GetBooksAsync(new BookListQuery { Search = "alice" })).TotalCount);
    }

    [Fact]
    public async Task Users_CannotReadChangeOrDeleteSomeoneElsesItems()
    {
        var alice = (await app.CreateUserAsync()).Api;
        var bob = (await app.CreateUserAsync()).Api;
        var author = await alice.AddAuthorAsync("Private author");
        var tag = await alice.AddTagAsync("Private tag");
        var book = await alice.AddBookAsync("Private book", [author.Id], [tag.Id]);
        var read = await alice.CreateReadAsync(book.Id, new CreateReadRequest());

        await AssertNotFound(() => bob.GetBookAsync(book.Id));
        await AssertNotFound(() => bob.UpdateBookAsync(book.Id, book.ToUpdate()));
        await AssertNotFound(() => bob.DeleteBookAsync(book.Id));
        await AssertNotFound(() => bob.GetAuthorAsync(author.Id));
        await AssertNotFound(() => bob.DeleteAuthorAsync(author.Id));
        await AssertNotFound(() => bob.UpdateTagAsync(tag.Id, new UpdateTagRequest { Name = "Hijacked", Version = tag.Version }));
        await AssertNotFound(() => bob.DeleteTagAsync(tag.Id));
        await AssertNotFound(() => bob.GetReadsAsync(book.Id));
        await AssertNotFound(() => bob.CreateReadAsync(book.Id, new CreateReadRequest()));
        await AssertNotFound(() => bob.DeleteReadAsync(book.Id, read.Id));

        var stillThere = await alice.GetBookAsync(book.Id);
        Assert.Equal("Private book", stillThere.Title);
        Assert.Single(stillThere.Reads);
        Assert.Single(stillThere.Tags);
        Assert.Single(stillThere.Authors);
    }

    [Fact]
    public async Task Users_CannotAttachSomeoneElsesAuthorsOrTags()
    {
        var alice = (await app.CreateUserAsync()).Api;
        var bob = (await app.CreateUserAsync()).Api;
        var aliceAuthor = await alice.AddAuthorAsync("Alice's author");
        var aliceTag = await alice.AddTagAsync("Alice's tag");

        var authorError = await Assert.ThrowsAsync<ApiException>(() => bob.AddBookAsync("Bob's book", authorIds: [aliceAuthor.Id]));
        var tagError = await Assert.ThrowsAsync<ApiException>(() => bob.AddBookAsync("Bob's book", tagIds: [aliceTag.Id]));

        Assert.Equal(HttpStatusCode.BadRequest, authorError.StatusCode);
        Assert.Contains("authorIds", authorError.Errors.Keys);
        Assert.Equal(HttpStatusCode.BadRequest, tagError.StatusCode);
        Assert.Contains("tagIds", tagError.Errors.Keys);
    }

    private static async Task AssertNotFound(Func<Task> call)
    {
        var error = await Assert.ThrowsAsync<ApiException>(call);
        Assert.Equal(HttpStatusCode.NotFound, error.StatusCode);
    }
}
