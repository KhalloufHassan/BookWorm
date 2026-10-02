using System.Security.Claims;
using BookWorm.Contracts;
using BookWorm.UI.Api;
using BookWorm.UI.Auth;
using Microsoft.AspNetCore.Components.Authorization;

namespace BookWorm.Client.Services;

/// <summary>Asks the server who is signed in (via the session cookie) and exposes it to the UI.</summary>
public sealed class CookieAuthenticationStateProvider(BookWormApiClient api) : AuthenticationStateProvider
{
    private static readonly AuthenticationState Anonymous = new(new ClaimsPrincipal(new ClaimsIdentity()));

    private Task<AuthenticationState> _state;

    public override Task<AuthenticationState> GetAuthenticationStateAsync() => _state ??= LoadAsync();

    public void MarkSignedOut()
    {
        _state = Task.FromResult(Anonymous);
        NotifyAuthenticationStateChanged(_state);
    }

    private async Task<AuthenticationState> LoadAsync()
    {
        CurrentUser user;
        try
        {
            user = await api.GetCurrentUserAsync();
        }
        catch (HttpRequestException)
        {
            return Anonymous;
        }

        if (user is null)
        {
            return Anonymous;
        }

        return new AuthenticationState(UserPrincipal.From(user));
    }
}
