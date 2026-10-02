using BookWorm.Mobile.Core.Auth;
using BookWorm.UI.Api;
using BookWorm.UI.Auth;
using Microsoft.AspNetCore.Components.Authorization;

namespace BookWorm.Mobile.Services;

/// <summary>
/// Who is signed in, for the pages. Asks the server once per sign-in (to catch deleted accounts and
/// role changes) but trusts the saved user while offline, so downloaded books stay readable.
/// </summary>
public sealed class MobileAuthenticationStateProvider : AuthenticationStateProvider, IDisposable
{
    private readonly SessionManager _session;
    private readonly BookWormApiClient _api;
    private bool _checkedWithServer;

    public MobileAuthenticationStateProvider(SessionManager session, BookWormApiClient api)
    {
        _session = session;
        _api = api;
        _session.Changed += OnSessionChanged;
    }

    public override async Task<AuthenticationState> GetAuthenticationStateAsync()
    {
        await _session.InitializeAsync();
        if (!_session.IsSignedIn)
        {
            return new AuthenticationState(UserPrincipal.Anonymous);
        }

        if (!_checkedWithServer)
        {
            try
            {
                var user = await _api.GetCurrentUserAsync();
                _checkedWithServer = true;
                if (user is null)
                {
                    await _session.SignOutAsync();
                    return new AuthenticationState(UserPrincipal.Anonymous);
                }

                await _session.UpdateUserAsync(user);
            }
            catch (HttpRequestException)
            {
                // Offline or unreachable: the saved user it is, for now.
            }
        }

        return _session.User is { } current
            ? new AuthenticationState(UserPrincipal.From(current))
            : new AuthenticationState(UserPrincipal.Anonymous);
    }

    private void OnSessionChanged()
    {
        _checkedWithServer = false;
        NotifyAuthenticationStateChanged(GetAuthenticationStateAsync());
    }

    public void Dispose() => _session.Changed -= OnSessionChanged;
}
