using System.Net;
using BookWorm.Contracts;
using BookWorm.Server.Tests.Infrastructure;
using BookWorm.UI.Api;

namespace BookWorm.Server.Tests;

public sealed class TagsApiTests(BookWormAppFactory app)
{
    [Fact]
    public async Task Names_AreUniquePerUserIgnoringCase()
    {
        var alice = (await app.CreateUserAsync()).Api;
        var bob = (await app.CreateUserAsync()).Api;
        await alice.AddTagAsync("Fantasy");

        var duplicate = await Assert.ThrowsAsync<ApiException>(() => alice.AddTagAsync("fantasy"));
        var bobsTag = await bob.AddTagAsync("Fantasy");

        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        Assert.Equal("Fantasy", bobsTag.Name);
    }

    [Fact]
    public async Task Rename_RejectsTakenNamesButAllowsChangingCase()
    {
        var api = (await app.CreateUserAsync()).Api;
        await api.AddTagAsync("Sci-fi");
        var space = await api.AddTagAsync("space");

        var taken = await Assert.ThrowsAsync<ApiException>(() =>
            api.UpdateTagAsync(space.Id, new UpdateTagRequest { Name = "SCI-FI", Version = space.Version }));
        var renamed = await api.UpdateTagAsync(space.Id, new UpdateTagRequest { Name = "Space", Version = space.Version });

        Assert.Equal(HttpStatusCode.Conflict, taken.StatusCode);
        Assert.Equal("Space", renamed.Name);
    }

    [Fact]
    public async Task Rename_WithAStaleVersion_IsRejected()
    {
        var api = (await app.CreateUserAsync()).Api;
        var tag = await api.AddTagAsync("Poetry");
        await api.UpdateTagAsync(tag.Id, new UpdateTagRequest { Name = "Poems", Version = tag.Version });

        var error = await Assert.ThrowsAsync<ApiException>(() =>
            api.UpdateTagAsync(tag.Id, new UpdateTagRequest { Name = "Verse", Version = tag.Version }));

        Assert.Equal(HttpStatusCode.Conflict, error.StatusCode);
    }

    [Fact]
    public async Task List_CountsBooksAndFiltersByName()
    {
        var api = (await app.CreateUserAsync()).Api;
        var horror = await api.AddTagAsync("Horror");
        await api.AddTagAsync("Humour");
        await api.AddBookAsync("It", tagIds: [horror.Id]);
        await api.AddBookAsync("Carrie", tagIds: [horror.Id]);

        var all = await api.GetTagsAsync();
        var filtered = await api.GetTagsAsync("horr");

        Assert.Equal(["Horror", "Humour"], all.Select(t => t.Name));
        Assert.Equal(2, all[0].BookCount);
        Assert.Equal(["Horror"], filtered.Select(t => t.Name));
    }

    [Fact]
    public async Task Delete_RemovesTheTagFromBooks()
    {
        var api = (await app.CreateUserAsync()).Api;
        var tag = await api.AddTagAsync("Temporary");
        var book = await api.AddBookAsync("Kept book", tagIds: [tag.Id]);

        await api.DeleteTagAsync(tag.Id);

        Assert.Empty((await api.GetBookAsync(book.Id)).Tags);
    }
}
