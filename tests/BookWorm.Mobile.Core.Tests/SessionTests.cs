using System.Net;
using BookWorm.Contracts;
using BookWorm.Mobile.Core.Auth;
using BookWorm.UI.Api;
using BookWorm.UI.Services;

namespace BookWorm.Mobile.Core.Tests;

public sealed class SessionTests
{
    private static readonly CurrentUser Reader = new(Guid.NewGuid(), "reader", null, false);

    [Theory]
    [InlineData("books.example.org", "https://books.example.org/")]
    [InlineData(" https://books.example.org ", "https://books.example.org/")]
    [InlineData("https://example.org/bookworm", "https://example.org/bookworm/")]
    [InlineData("https://books.example.org:8443/?x=1#y", "https://books.example.org:8443/")]
    public void ServerAddresses_AreNormalized(string input, string expected) =>
        Assert.Equal(expected, ServerAddress.Normalize(input));

    [Theory]
    [InlineData("http://books.example.org")]
    [InlineData("ftp://books.example.org")]
    [InlineData("")]
    public void ServerAddresses_MustBeHttps(string input) =>
        Assert.Throws<AppSignInException>(() => ServerAddress.Normalize(input));

    [Theory]
    [InlineData("1.0.0", "1.2.0", true)]
    [InlineData("1.2.0", "1.2.0", false)]
    [InlineData("1.3.0", "1.2.0", false)]
    [InlineData("1.0.0", null, false)]
    public void AppVersions_AreCompared(string current, string minimum, bool older) =>
        Assert.Equal(older, SessionManager.IsOlder(current, minimum));

    [Fact]
    public async Task SettingTheServer_RejectsServersThatNeedANewerApp()
    {
        var server = new FakeServer
        {
            Respond = (_, _) => FakeServer.Json(new ServerInfo("BookWorm", "2.0.0", ApiLimits.CurrentApiVersion, false, "2.0.0")),
        };
        var session = NewSession(server, new MemorySessionStorage(), new FakeTime(DateTimeOffset.UtcNow));

        var error = await Assert.ThrowsAsync<AppSignInException>(() => session.SetServerAsync("books.test"));

        Assert.Contains("newer version of the app", error.Message);
    }

    [Fact]
    public async Task SigningIn_KeepsTheTokensAndTheUser()
    {
        var server = new FakeServer
        {
            Respond = (request, _) => request.RequestUri.AbsolutePath switch
            {
                "/api/auth/login" => FakeServer.Json(new AppTokens("Bearer", "access-1", 1800, "refresh-1")),
                "/api/me" => FakeServer.Json(Reader),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            },
        };
        var storage = new MemorySessionStorage { Address = FakeServer.Address };
        var session = NewSession(server, storage, new FakeTime(DateTimeOffset.UtcNow));
        await session.InitializeAsync();

        await session.SignInAsync(new AppLoginRequest { UserName = "reader", Password = "secret" });

        Assert.Equal("access-1", storage.Session.AccessToken);
        Assert.Equal(Reader, session.User);
        Assert.Equal("Bearer access-1", server.Requests.Single(r => r.Path == "api/me").Authorization);
    }

    [Fact]
    public async Task ExpiringTokens_AreRefreshedOnce_ForManyRequestsAtOnce()
    {
        var time = new FakeTime(DateTimeOffset.UtcNow);
        var refreshes = 0;
        var server = new FakeServer
        {
            Respond = (request, _) =>
            {
                if (request.RequestUri.AbsolutePath == "/api/auth/refresh")
                {
                    Interlocked.Increment(ref refreshes);
                    Thread.Sleep(50);
                    return FakeServer.Json(new AppTokens("Bearer", "access-2", 1800, "refresh-2"));
                }

                return new HttpResponseMessage(HttpStatusCode.NoContent);
            },
        };
        var storage = new MemorySessionStorage { Address = FakeServer.Address, Session = new AppSession("access-1", "refresh-1", time.Now.AddSeconds(30), Reader) };
        var session = NewSession(server, storage, time);
        await session.InitializeAsync();

        var tokens = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => session.GetAccessTokenAsync())));

        Assert.All(tokens, token => Assert.Equal("access-2", token));
        Assert.Equal(1, refreshes);
        Assert.Equal("refresh-2", storage.Session.RefreshToken);
    }

    [Fact]
    public async Task ARejectedRefresh_SignsOut()
    {
        var time = new FakeTime(DateTimeOffset.UtcNow);
        var server = new FakeServer { Respond = (_, _) => new HttpResponseMessage(HttpStatusCode.Unauthorized) };
        var storage = new MemorySessionStorage { Address = FakeServer.Address, Session = new AppSession("a", "r", time.Now.AddSeconds(-1), Reader) };
        var session = NewSession(server, storage, time);
        await session.InitializeAsync();
        var signedOut = false;
        session.Changed += () => signedOut = !session.IsSignedIn;

        Assert.Null(await session.GetAccessTokenAsync());
        Assert.True(signedOut);
        Assert.Null(storage.Session);
    }

    [Fact]
    public async Task Offline_AnExpiredTokenIsKept_SoTheAppStaysSignedIn()
    {
        var time = new FakeTime(DateTimeOffset.UtcNow);
        var server = new FakeServer { Unreachable = true };
        var storage = new MemorySessionStorage { Address = FakeServer.Address, Session = new AppSession("a", "r", time.Now.AddSeconds(-1), Reader) };
        var session = NewSession(server, storage, time);
        await session.InitializeAsync();

        Assert.Equal("a", await session.GetAccessTokenAsync());
        Assert.True(session.IsSignedIn);
    }

    [Fact]
    public async Task A401_RefreshesAndRetriesTheRequestOnce()
    {
        var time = new FakeTime(DateTimeOffset.UtcNow);
        var server = new FakeServer
        {
            Respond = (request, body) => request.RequestUri.AbsolutePath == "/api/auth/refresh"
                ? FakeServer.Json(new AppTokens("Bearer", "fresh", 1800, "r2"))
                : request.Headers.Authorization?.Parameter == "fresh"
                    ? FakeServer.Json(new { echoed = body })
                    : new HttpResponseMessage(HttpStatusCode.Unauthorized),
        };
        var storage = new MemorySessionStorage { Address = FakeServer.Address, Session = new AppSession("revoked", "r", time.Now.AddHours(1), Reader) };
        var session = NewSession(server, storage, time);
        await session.InitializeAsync();
        using var http = server.Client(new BearerTokenHandler(session) { InnerHandler = server });

        using var response = await http.PostAsync("api/tags", new StringContent("{\"name\":\"sea\"}", System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("sea", await response.Content.ReadAsStringAsync());
        Assert.Equal(2, server.Requests.Count(r => r.Path == "api/tags"));
    }

    [Fact]
    public void Pkce_ChallengeIsTheHashOfTheVerifier()
    {
        var (verifier, challenge) = Pkce.Create();

        Assert.Equal(43, verifier.Length);
        Assert.Equal(43, challenge.Length);
        Assert.Equal(challenge, Pkce.ChallengeFor(verifier));
    }

    private static SessionManager NewSession(FakeServer server, MemorySessionStorage storage, FakeTime time) =>
        new(storage, address => new HttpClient(server, disposeHandler: false) { BaseAddress = new Uri(address) }, time, "1.0.0");
}

public sealed class ReaderLibrariesTests
{
    [Fact]
    public void Versions_ComeFromTheBuild()
    {
        Assert.Equal("6.3.289", BookWorm.UI.Services.ReaderLibraries.PdfJsVersion);
        Assert.Equal(40, BookWorm.UI.Services.ReaderLibraries.FoliateJsCommit.Length);
        Assert.EndsWith($"@{BookWorm.UI.Services.ReaderLibraries.FoliateJsCommit}/", BookWorm.UI.Services.ReaderLibraries.CdnImports["foliate-js/"]);
    }
}

public sealed class ServerAddressHandlerTests
{
    [Theory]
    [InlineData("https://books.test/", "https://books.test/api/books?page=2")]
    [InlineData("https://example.test/bookworm/", "https://example.test/bookworm/api/books?page=2")]
    public async Task Requests_GoToTheChosenServer(string address, string expected)
    {
        var server = new FakeServer();
        var storage = new MemorySessionStorage { Address = address };
        var session = new BookWorm.Mobile.Core.Auth.SessionManager(storage, _ => new HttpClient(server), TimeProvider.System, "1.0.0");
        await session.InitializeAsync();
        string sent = null;
        server.Respond = (request, _) =>
        {
            sent = request.RequestUri.ToString();
            return new HttpResponseMessage(System.Net.HttpStatusCode.NoContent);
        };
        using var http = new HttpClient(new BookWorm.Mobile.Core.Auth.ServerAddressHandler(session) { InnerHandler = server })
        {
            BaseAddress = BookWorm.Mobile.Core.Auth.ServerAddressHandler.Placeholder,
        };

        await http.GetAsync("api/books?page=2");

        Assert.Equal(expected, sent);
    }
}
