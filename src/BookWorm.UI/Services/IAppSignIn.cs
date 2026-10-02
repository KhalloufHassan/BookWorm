using BookWorm.Contracts;

namespace BookWorm.UI.Services;

/// <summary>
/// Signing in from the mobile app's own sign-in page (<c>/app-login</c>). The web app signs in on the
/// server's login pages instead and doesn't provide this.
/// </summary>
public interface IAppSignIn
{
    /// <summary>The server the app talks to, e.g. "https://books.example.org/"; null until chosen.</summary>
    string ServerAddress { get; }

    /// <summary>
    /// Checks that a BookWorm server answers at the address (HTTPS only) and that this app works with it,
    /// then remembers it. Throws <see cref="AppSignInException"/> with a message for the user otherwise.
    /// </summary>
    Task<ServerInfo> SetServerAsync(string address);

    /// <summary>Signs in with a user name and password. Throws <c>ApiException</c> with an <see cref="AuthProblemTypes"/> type when it fails.</summary>
    Task SignInAsync(AppLoginRequest request);

    /// <summary>Signs in on the server's login page in the system browser, where passkeys work.</summary>
    Task SignInWithBrowserAsync();
}

public sealed class AppSignInException(string message) : Exception(message);
