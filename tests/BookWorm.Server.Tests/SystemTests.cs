using System.Net;
using System.Net.Http.Json;
using BookWorm.Contracts;
using BookWorm.Server.Tests.Infrastructure;
using BookWorm.UI.Api;

namespace BookWorm.Server.Tests;

public sealed class SystemTests(BookWormAppFactory app)
{
    [Fact]
    public async Task ServerInfo_IsPublicAndReportsTheApiVersion()
    {
        await app.CreateUserAsync(); // setup is complete once any account exists
        var api = new BookWormApiClient(app.CreateClient());

        var info = await api.GetServerInfoAsync();

        Assert.Equal("BookWorm", info.Name);
        Assert.Equal(1, info.ApiVersion);
        Assert.False(string.IsNullOrWhiteSpace(info.Version));
        Assert.False(info.SetupRequired);
    }

    [Fact]
    public async Task Health_IsHealthyWhenTheDatabaseIsReachable()
    {
        var response = await app.CreateClient().GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Healthy", await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("/api/books")]
    [InlineData("/api/authors")]
    [InlineData("/api/tags")]
    [InlineData("/api/me")]
    public async Task Api_RequiresSignIn(string path)
    {
        var response = await app.CreateClient().GetAsync(path);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Me_ReturnsTheSignedInUser()
    {
        var user = await app.CreateUserAsync();

        var me = await user.Api.GetCurrentUserAsync();

        Assert.NotNull(me);
        Assert.Equal(user.Id, me.Id);
        Assert.Equal(user.UserName, me.UserName);
        Assert.False(me.IsAdmin);
    }

    [Fact]
    public async Task UnknownApiRoutes_AnswerWithProblemDetails()
    {
        var user = await app.CreateUserAsync();

        var response = await user.Http.GetAsync($"/api/books/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ApiProblem>();
        Assert.Equal(404, problem?.Status);
    }

    [Theory]
    [InlineData("/api/server-info")]
    [InlineData("/Account/Login")]
    public async Task Responses_CarrySecurityHeaders(string path)
    {
        await app.CreateUserAsync(); // past the first-run setup redirect

        var response = await app.CreateClient().GetAsync(path);

        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("DENY", response.Headers.GetValues("X-Frame-Options").Single());
        Assert.Equal("same-origin", response.Headers.GetValues("Referrer-Policy").Single());
    }

    [Fact]
    public async Task OpenApiDocument_DescribesTheApi()
    {
        var document = await app.CreateClient().GetStringAsync("/openapi/bookworm.json");

        Assert.Contains("\"/api/books\"", document);
        Assert.Contains("\"/api/admin/users\"", document);
        Assert.DoesNotContain("PasskeyRequestOptions", document);
    }
}
