using System.Security.Claims;
using BookWorm.Contracts;
using BookWorm.UI.Api;
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

        List<Claim> claims =
        [
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new(ClaimTypes.Name, user.UserName),
        ];

        if (user.Email is not null)
        {
            claims.Add(new Claim(ClaimTypes.Email, user.Email));
        }

        if (user.IsAdmin)
        {
            claims.Add(new Claim(ClaimTypes.Role, RoleNames.Admin));
        }

        return new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType: "BookWorm")));
    }
}
