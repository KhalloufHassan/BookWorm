using System.Security.Claims;
using BookWorm.Contracts;
using BookWorm.Server.Auth;
using BookWorm.Server.Data;
using BookWorm.Server.Setup;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Identity;

namespace BookWorm.Server.Api;

internal static class ApiEndpoints
{
    /// <summary>Maps the whole HTTP API under <c>/api</c>. Everything except server info requires sign-in.</summary>
    public static void MapBookWormApi(this IEndpointRouteBuilder endpoints)
    {
        var api = endpoints.MapGroup("/api");

        api.MapGet("/server-info", GetServerInfo)
            .AllowAnonymous()
            .WithTags("Server")
            .WithSummary("Server name, version and API version. Apps call this to check compatibility.");

        var signedIn = api.MapGroup("").RequireAuthorization();

        var account = signedIn.MapGroup("").WithTags("Account");
        account.MapGet("/me", GetCurrentUser).WithSummary("The signed-in user.");
        account.MapPost("/account/logout", Logout).WithSummary("Sign out of this session.");

        signedIn.MapBookEndpoints();
        signedIn.MapFileEndpoints();
        signedIn.MapReadEndpoints();
        signedIn.MapHighlightEndpoints();
        signedIn.MapNotesEndpoints();
        signedIn.MapAuthorEndpoints();
        signedIn.MapTagEndpoints();
        signedIn.MapStatsEndpoints();
        signedIn.MapAdminUserEndpoints();
        signedIn.MapBackupEndpoints();

        api.MapSetupRestoreEndpoints();
    }

    private static async Task<Ok<ServerInfo>> GetServerInfo(SetupState setupState, CancellationToken cancellationToken) =>
        TypedResults.Ok(new ServerInfo(
            "BookWorm",
            ServerVersion.Current,
            ApiLimits.CurrentApiVersion,
            await setupState.IsSetupRequiredAsync(cancellationToken)));

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

    private static async Task<NoContent> Logout(SignInManager<AppUser> signInManager)
    {
        await signInManager.SignOutAsync();
        return TypedResults.NoContent();
    }
}
