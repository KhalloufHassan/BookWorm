using System.Net;
using Microsoft.AspNetCore.Components;

namespace BookWorm.Client.Services;

/// <summary>
/// When an API call answers 401 the session has ended (expired, password reset, account deleted),
/// so send the user to the login page and bring them back here afterwards.
/// </summary>
internal sealed class SessionExpiredHandler(NavigationManager navigation) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var response = await base.SendAsync(request, cancellationToken);

        // /api/me answering 401 just means "not signed in"; the router handles that itself.
        if (response.StatusCode == HttpStatusCode.Unauthorized && request.RequestUri?.AbsolutePath.EndsWith("/api/me", StringComparison.Ordinal) != true)
        {
            var returnUrl = navigation.ToBaseRelativePath(navigation.Uri);
            navigation.NavigateTo($"Account/Login?returnUrl={Uri.EscapeDataString(returnUrl)}", forceLoad: true);
        }

        return response;
    }
}
