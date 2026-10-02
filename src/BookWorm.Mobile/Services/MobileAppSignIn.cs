using BookWorm.Contracts;
using BookWorm.Mobile.Core.Auth;
using BookWorm.Mobile.Core.Offline;
using BookWorm.UI.Services;
using Microsoft.Maui.Authentication;

namespace BookWorm.Mobile.Services;

/// <summary>Signing in from the app's sign-in page: with a password in the app, or in the browser (passkeys).</summary>
public sealed class MobileAppSignIn(SessionManager session, SyncService sync) : IAppSignIn
{
    public string ServerAddress => session.ServerAddress;

    public Task<ServerInfo> SetServerAsync(string address) => session.SetServerAsync(address);

    public async Task SignInAsync(AppLoginRequest request)
    {
        await session.SignInAsync(request);
        _ = sync.SyncAsync();
    }

    /// <summary>
    /// Opens the server's sign-in page in the browser (a Custom Tab). After signing in there and
    /// confirming, the server sends the browser to bookworm://auth with a one-time code, which only
    /// becomes tokens together with the PKCE verifier kept here.
    /// </summary>
    public async Task SignInWithBrowserAsync()
    {
        if (session.ServerAddress is null)
        {
            throw new AppSignInException("Choose your server first.");
        }

        var (verifier, challenge) = Pkce.Create();
        var start = new Uri(new Uri(session.ServerAddress), $"{MobileSignIn.StartPath}?challenge={challenge}");
        var result = await WebAuthenticator.Default.AuthenticateAsync(new WebAuthenticatorOptions
        {
            Url = start,
            CallbackUrl = new Uri(MobileSignIn.CallbackUri),
            PrefersEphemeralWebBrowserSession = false,
        });

        if (!result.Properties.TryGetValue("code", out var code) || string.IsNullOrEmpty(code))
        {
            throw new AppSignInException("The browser didn't finish signing in. Try again.");
        }

        await session.SignInWithCodeAsync(code, verifier);
        _ = sync.SyncAsync();
    }
}
