namespace BookWorm.UI.Services;

/// <summary>
/// Sign-in related actions that work differently per host: the web app uses the server's login pages
/// and a session cookie; the mobile app will use the system browser and tokens.
/// </summary>
public interface IAccountService
{
    /// <summary>Send the user to sign in, coming back to the current page afterwards.</summary>
    void RedirectToLogin();

    /// <summary>Open the account settings (password, two-factor authentication, passkeys).</summary>
    void OpenAccountSettings();

    Task SignOutAsync();

    /// <summary>Signs out on every device and browser (a new security stamp), then here too.</summary>
    Task SignOutEverywhereAsync();
}
