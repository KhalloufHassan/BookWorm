namespace BookWorm.Server.Setup;

internal static class SetupRedirect
{
    /// <summary>Until the first administrator exists, send visitors of the app or login pages to /setup.</summary>
    public static IApplicationBuilder UseSetupRedirect(this IApplicationBuilder app) => app.Use(async (context, next) =>
    {
        var path = context.Request.Path;
        if (HttpMethods.IsGet(context.Request.Method) && (path == "/" || path.StartsWithSegments("/Account")))
        {
            var setupState = context.RequestServices.GetRequiredService<SetupState>();
            if (await setupState.IsSetupRequiredAsync(context.RequestAborted))
            {
                context.Response.Redirect($"{context.Request.PathBase}/setup");
                return;
            }
        }

        await next(context);
    });
}
