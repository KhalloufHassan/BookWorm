using BookWorm.UI.Api;
using BookWorm.UI.Services;
using Microsoft.AspNetCore.Components;

namespace BookWorm.Client.Services;

/// <summary>The web app signs in on the server's own login pages and keeps the session in a cookie.</summary>
internal sealed class WebAccountService(
    NavigationManager navigation,
    BookWormApiClient api,
    CookieAuthenticationStateProvider authenticationState) : IAccountService
{
    public void RedirectToLogin()
    {
        var returnUrl = navigation.ToBaseRelativePath(navigation.Uri);
        navigation.NavigateTo($"Account/Login?returnUrl={Uri.EscapeDataString(returnUrl)}", forceLoad: true);
    }

    public void OpenAccountSettings() => navigation.NavigateTo("Account/Manage", forceLoad: true);

    public async Task SignOutAsync()
    {
        await api.LogoutAsync();
        authenticationState.MarkSignedOut();
        navigation.NavigateTo("Account/Login", forceLoad: true);
    }
}
