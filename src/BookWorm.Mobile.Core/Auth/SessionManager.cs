using System.Net;
using BookWorm.Contracts;
using BookWorm.UI.Api;
using BookWorm.UI.Services;

namespace BookWorm.Mobile.Core.Auth;

/// <summary>
/// The app's server address and sign-in: signs in, keeps the tokens fresh (one refresh at a time,
/// however many requests need it) and signs out when the server no longer accepts them.
/// </summary>
/// <param name="createClient">An HttpClient for the given server address, without the app's own handlers.</param>
public sealed class SessionManager(ISessionStorage storage, Func<string, HttpClient> createClient, TimeProvider timeProvider, string appVersion)
{
    /// <summary>Tokens are refreshed this long before they expire, so requests don't race the expiry.</summary>
    private static readonly TimeSpan RefreshMargin = TimeSpan.FromMinutes(2);

    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private bool _initialized;

    public string ServerAddress { get; private set; }

    public AppSession Session { get; private set; }

    public CurrentUser User => Session?.User;

    public bool IsSignedIn => Session is not null;

    /// <summary>Signed in or out (including when the server rejected the session).</summary>
    public event Action Changed;

    public async Task InitializeAsync()
    {
        if (_initialized)
        {
            return;
        }

        ServerAddress = await storage.GetServerAddressAsync();
        Session = ServerAddress is null ? null : await storage.LoadSessionAsync();
        _initialized = true;
    }

    /// <summary>Checks the server and this app work together, then remembers it. Changing server signs out.</summary>
    public async Task<ServerInfo> SetServerAsync(string input)
    {
        var address = Auth.ServerAddress.Normalize(input);
        ServerInfo info;
        try
        {
            using var http = createClient(address);
            info = await new BookWormApiClient(http).GetServerInfoAsync();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or ApiException)
        {
            throw new AppSignInException("No BookWorm server answered at that address. Check it, and that this phone can reach it.");
        }

        if (info?.Name != "BookWorm")
        {
            throw new AppSignInException("That address isn't a BookWorm server.");
        }

        if (info.ApiVersion > ApiLimits.CurrentApiVersion || IsOlder(appVersion, info.MinimumAppVersion))
        {
            throw new AppSignInException("This server needs a newer version of the app. Update the app, then try again.");
        }

        if (info.ApiVersion < ApiLimits.CurrentApiVersion)
        {
            throw new AppSignInException("This server runs an older BookWorm. Update the server, then try again.");
        }

        if (info.SetupRequired)
        {
            throw new AppSignInException("This server isn't set up yet. Open it in a browser to create the first account.");
        }

        if (address != ServerAddress)
        {
            await ClearSessionAsync();
            ServerAddress = address;
            await storage.SetServerAddressAsync(address);
        }

        return info;
    }

    public async Task SignInAsync(AppLoginRequest request)
    {
        var tokens = await WithPlainApi(api => api.LoginAsync(request));
        await CompleteSignInAsync(tokens);
    }

    /// <summary>Finishes the browser sign-in: exchanges its one-time code, with the verifier only this app knows.</summary>
    public async Task SignInWithCodeAsync(string code, string codeVerifier)
    {
        var tokens = await WithPlainApi(api => api.RedeemMobileCodeAsync(new MobileCodeRequest { Code = code, CodeVerifier = codeVerifier }));
        await CompleteSignInAsync(tokens);
    }

    /// <summary>
    /// A current access token, refreshed first when it's about to expire (or <paramref name="force"/>).
    /// Null when signed out. When the server can't be reached, the current token is returned as it is.
    /// </summary>
    public async Task<string> GetAccessTokenAsync(bool force = false, CancellationToken cancellationToken = default)
    {
        var session = Session;
        if (session is null)
        {
            return null;
        }

        if (!force && session.AccessTokenExpiresAt - RefreshMargin > timeProvider.GetUtcNow())
        {
            return session.AccessToken;
        }

        await _refreshLock.WaitAsync(cancellationToken);
        try
        {
            // Another request may have refreshed while this one waited.
            if (Session is null)
            {
                return null;
            }

            if (Session != session && Session.AccessTokenExpiresAt - RefreshMargin > timeProvider.GetUtcNow())
            {
                return Session.AccessToken;
            }

            try
            {
                var tokens = await WithPlainApi(api => api.RefreshTokensAsync(Session.RefreshToken, cancellationToken));
                Session = ToSession(tokens, Session.User);
                await storage.SaveSessionAsync(Session);
                return Session.AccessToken;
            }
            catch (ApiException ex) when (ex.StatusCode == HttpStatusCode.Unauthorized)
            {
                // Signed out everywhere, password changed, or unused for too long.
                await ClearSessionAsync();
                return null;
            }
            catch (HttpRequestException)
            {
                return Session?.AccessToken;
            }
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    /// <summary>Keeps the saved user up to date (name or role changes) after a successful <c>/api/me</c>.</summary>
    public async Task UpdateUserAsync(CurrentUser user)
    {
        if (Session is not null && user is not null && user != Session.User)
        {
            Session = Session with { User = user };
            await storage.SaveSessionAsync(Session);
        }
    }

    public Task SignOutAsync() => ClearSessionAsync();

    private async Task CompleteSignInAsync(AppTokens tokens)
    {
        using var http = createClient(ServerAddress);
        http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        var user = await new BookWormApiClient(http).GetCurrentUserAsync()
            ?? throw new AppSignInException("The server didn't accept the sign-in. Try again.");

        Session = ToSession(tokens, user);
        await storage.SaveSessionAsync(Session);
        Changed?.Invoke();
    }

    private async Task ClearSessionAsync()
    {
        var wasSignedIn = Session is not null;
        Session = null;
        await storage.ClearSessionAsync();
        if (wasSignedIn)
        {
            Changed?.Invoke();
        }
    }

    private AppSession ToSession(AppTokens tokens, CurrentUser user) =>
        new(tokens.AccessToken, tokens.RefreshToken, timeProvider.GetUtcNow().AddSeconds(tokens.ExpiresIn), user);

    private async Task<T> WithPlainApi<T>(Func<BookWormApiClient, Task<T>> call)
    {
        if (ServerAddress is null)
        {
            throw new AppSignInException("Choose your server first.");
        }

        using var http = createClient(ServerAddress);
        return await call(new BookWormApiClient(http));
    }

    /// <summary>Whether version <paramref name="current"/> is older than <paramref name="minimum"/> (e.g. "1.2.0").</summary>
    internal static bool IsOlder(string current, string minimum) =>
        Version.TryParse(minimum, out var min) && Version.TryParse(current, out var version) && version < min;
}
