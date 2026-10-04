using System.Security.Claims;
using BookWorm.Contracts;
using BookWorm.Server.Auth;
using BookWorm.Server.Data;
using BookWorm.Server.Setup;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace BookWorm.Server.Api;

internal static class ApiEndpoints
{
    /// <summary>Maps the whole HTTP API under <c>/api</c>. Everything except server info requires sign-in.</summary>
    public static void MapBookWormApi(this IEndpointRouteBuilder endpoints)
    {
        var api = endpoints.MapGroup("/api").RequireCors(AppClientOptions.CorsPolicy);

        api.MapGet("/server-info", GetServerInfo)
            .AllowAnonymous()
            .WithTags("Server")
            .WithSummary("Server name, version and API version. Apps call this to check compatibility.");

        api.MapAuthEndpoints();

        var signedIn = api.MapGroup("").RequireAuthorization();

        var account = signedIn.MapGroup("").WithTags("Account");
        account.MapGet("/me", GetCurrentUser).WithSummary("The signed-in user.");
        account.MapPost("/account/logout", Logout).WithSummary("Sign out of this session.");
        account.MapPost("/account/sign-out-everywhere", SignOutEverywhere)
            .WithSummary("Sign out on every device and browser, including apps (their access tokens stop working within 30 minutes).");

        signedIn.MapBookEndpoints();
        signedIn.MapFileEndpoints();
        signedIn.MapReadEndpoints();
        signedIn.MapHighlightEndpoints();
        signedIn.MapNotesEndpoints();
        signedIn.MapAuthorEndpoints();
        signedIn.MapTagEndpoints();
        signedIn.MapCollectionEndpoints();
        signedIn.MapStatsEndpoints();
        signedIn.MapAdminUserEndpoints();
        signedIn.MapBackupEndpoints();

        api.MapSetupRestoreEndpoints();
    }

    private static async Task<Ok<ServerInfo>> GetServerInfo(
        SetupState setupState, IOptions<AppClientOptions> appOptions, CancellationToken cancellationToken) =>
        TypedResults.Ok(new ServerInfo(
            "BookWorm",
            ServerVersion.Current,
            ApiLimits.CurrentApiVersion,
            await setupState.IsSetupRequiredAsync(cancellationToken),
            string.IsNullOrWhiteSpace(appOptions.Value.MinimumVersion) ? null : appOptions.Value.MinimumVersion.Trim()));

    private static async Task<Results<Ok<CurrentUser>, UnauthorizedHttpResult>> GetCurrentUser(
        ClaimsPrincipal principal, UserManager<AppUser> userManager)
    {
        var user = await userManager.GetUserAsync(principal);
        if (user is null)
        {
            // Signed in with a cookie for an account that has since been deleted.
            return TypedResults.Unauthorized();
        }

        var isAdmin = await userManager.IsInRoleAsync(user, Roles.Admin);
        return TypedResults.Ok(new CurrentUser(user.Id, user.UserName, user.Email, isAdmin));
    }

    private static async Task<Results<NoContent, UnauthorizedHttpResult>> SignOutEverywhere(
        ClaimsPrincipal principal, UserManager<AppUser> userManager, SignInManager<AppUser> signInManager)
    {
        var user = await userManager.GetUserAsync(principal);
        if (user is null)
        {
            return TypedResults.Unauthorized();
        }

        // A new security stamp invalidates every session cookie and refresh token.
        await userManager.UpdateSecurityStampAsync(user);
        if (principal.Identity?.AuthenticationType == IdentityConstants.ApplicationScheme)
        {
            await signInManager.SignOutAsync();
        }

        return TypedResults.NoContent();
    }

    private static async Task<NoContent> Logout(SignInManager<AppUser> signInManager)
    {
        await signInManager.SignOutAsync();
        return TypedResults.NoContent();
    }
}
