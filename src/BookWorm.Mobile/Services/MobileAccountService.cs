using BookWorm.Mobile.Core.Auth;
using BookWorm.UI.Api;
using BookWorm.UI.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.Maui.ApplicationModel;

namespace BookWorm.Mobile.Services;

/// <summary>Sign-in actions in the app: the app's own sign-in page, and the server's account pages in the browser.</summary>
public sealed class MobileAccountService(NavigationManager navigation, SessionManager session, BookWormApiClient api) : IAccountService
{
    public void RedirectToLogin()
    {
        var returnUrl = navigation.ToBaseRelativePath(navigation.Uri);
        navigation.NavigateTo($"app-login?returnUrl={Uri.EscapeDataString(returnUrl)}", replace: true);
    }

    /// <summary>Password, two-factor authentication and passkeys are managed on the server's pages.</summary>
    public void OpenAccountSettings()
    {
        if (session.ServerAddress is { } server)
        {
            _ = Browser.Default.OpenAsync(new Uri(new Uri(server), "Account/Manage"), BrowserLaunchMode.SystemPreferred);
        }
    }

    public async Task SignOutAsync()
    {
        await session.SignOutAsync();
        navigation.NavigateTo("app-login", replace: true);
    }

    public async Task SignOutEverywhereAsync()
    {
        try
        {
            await api.SignOutEverywhereAsync();
        }
        finally
        {
            await SignOutAsync();
        }
    }
}
