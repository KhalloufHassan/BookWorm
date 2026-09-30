using System.Security.Claims;
using System.Text.Encodings.Web;
using BookWorm.Contracts;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BookWorm.Server.Tests.Infrastructure;

/// <summary>Signs requests in as the user named in a header, standing in for the login cookie.</summary>
internal sealed class TestAuthHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "Test";
    public const string UserHeader = "X-Test-User";
    public const string AdminHeader = "X-Test-Admin";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(UserHeader, out var userId))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        List<Claim> claims = [new(ClaimTypes.NameIdentifier, userId.ToString())];
        if (Request.Headers.ContainsKey(AdminHeader))
        {
            claims.Add(new Claim(ClaimTypes.Role, RoleNames.Admin));
        }

        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, SchemeName));
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName)));
    }
}
