namespace BookWorm.Server.Security;

internal static class SecurityHeaders
{
    /// <summary>
    /// Headers that make every response a bit safer in the browser: no guessing content types,
    /// no embedding BookWorm in other sites' frames (clickjacking), no leaking page addresses
    /// to other sites, and no access to camera, microphone or location.
    /// </summary>
    public static IApplicationBuilder UseSecurityHeaders(this IApplicationBuilder app) => app.Use(async (context, next) =>
    {
        var headers = context.Response.Headers;
        headers.XContentTypeOptions = "nosniff";
        headers.XFrameOptions = "DENY";
        headers["Referrer-Policy"] = "same-origin";
        headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=(), payment=()";
        await next(context);
    });
}
