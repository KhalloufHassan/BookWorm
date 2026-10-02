using Microsoft.AspNetCore.Identity;

namespace BookWorm.Server.Auth;

public static class AuthSchemes
{
    /// <summary>
    /// The default scheme: requests with an <c>Authorization: Bearer</c> header (the mobile app) are
    /// checked as bearer tokens, everything else with the web app's session cookie.
    /// </summary>
    public const string CookieOrBearer = "CookieOrBearer";

    public static string Select(HttpContext context) =>
        context.Request.Headers.Authorization.ToString().StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? IdentityConstants.BearerScheme
            : IdentityConstants.ApplicationScheme;
}
