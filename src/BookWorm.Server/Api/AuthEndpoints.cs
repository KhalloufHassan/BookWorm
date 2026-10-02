using BookWorm.Contracts;
using BookWorm.Server.Auth;
using BookWorm.Server.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.BearerToken;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace BookWorm.Server.Api;

/// <summary>
/// Sign-in for the mobile app, with bearer tokens instead of the web app's cookie. Built like
/// Identity's <c>MapIdentityApi</c> login and refresh, without its registration endpoints: accounts
/// are created by an administrator.
/// </summary>
internal static class AuthEndpoints
{
    public static void MapAuthEndpoints(this IEndpointRouteBuilder api)
    {
        var auth = api.MapGroup("/auth").WithTags("Auth").AllowAnonymous();

        auth.MapPost("/login", Login).WithValidation<AppLoginRequest>()
            .Produces<AppTokens>()
            .WithSummary("Sign an app in with a user name and password (and a two-factor code when the account has one).");
        auth.MapPost("/refresh", Refresh).WithValidation<RefreshTokenRequest>()
            .Produces<AppTokens>()
            .WithSummary("New tokens for a refresh token. Fails after a password change or \"sign out everywhere\".");
        auth.MapPost("/mobile-code", RedeemMobileCode).WithValidation<MobileCodeRequest>()
            .Produces<AppTokens>()
            .WithSummary("Exchange the one-time code from the browser sign-in (used for passkeys) for tokens.");
    }

    /// <summary>
    /// The last step of the mobile app's browser sign-in: the confirmation form on /Account/MobileLogin
    /// posts here, and the browser is sent back to the app with a one-time code.
    /// </summary>
    public static void MapMobileSignIn(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapPost("/" + MobileSignIn.StartPath + "/Confirm", ConfirmMobileSignIn).ExcludeFromDescription();

    private static async Task<IResult> Login(AppLoginRequest request, SignInManager<AppUser> signInManager)
    {
        // The bearer scheme makes a successful sign-in write the tokens as the response.
        signInManager.AuthenticationScheme = IdentityConstants.BearerScheme;
        var result = await signInManager.PasswordSignInAsync(request.UserName.Trim(), request.Password, isPersistent: false, lockoutOnFailure: true);

        var hasCode = !string.IsNullOrWhiteSpace(request.TwoFactorCode) || !string.IsNullOrWhiteSpace(request.RecoveryCode);
        if (result.RequiresTwoFactor && hasCode)
        {
            result = !string.IsNullOrWhiteSpace(request.TwoFactorCode)
                ? await signInManager.TwoFactorAuthenticatorSignInAsync(request.TwoFactorCode.Replace(" ", "").Replace("-", ""), isPersistent: false, rememberClient: false)
                : await signInManager.TwoFactorRecoveryCodeSignInAsync(request.RecoveryCode.Replace(" ", ""));
            if (result.Succeeded)
            {
                return TypedResults.Empty;
            }

            return result.IsLockedOut
                ? LoginProblem(AuthProblemTypes.LockedOut, "This account is locked for a few minutes after too many failed attempts.")
                : LoginProblem(AuthProblemTypes.InvalidTwoFactorCode, "That code isn't valid. Try again.");
        }

        if (result.Succeeded)
        {
            return TypedResults.Empty;
        }

        if (result.RequiresTwoFactor)
        {
            return LoginProblem(AuthProblemTypes.TwoFactorRequired, "Enter the code from your authenticator app.");
        }

        return result.IsLockedOut
            ? LoginProblem(AuthProblemTypes.LockedOut, "This account is locked for a few minutes after too many failed attempts.")
            : LoginProblem(AuthProblemTypes.InvalidCredentials, "The user name or password is wrong.");
    }

    private static async Task<IResult> Refresh(
        RefreshTokenRequest request,
        SignInManager<AppUser> signInManager,
        IOptionsMonitor<BearerTokenOptions> bearerOptions,
        TimeProvider timeProvider)
    {
        var protector = bearerOptions.Get(IdentityConstants.BearerScheme).RefreshTokenProtector;
        var ticket = protector.Unprotect(request.RefreshToken);

        // Rejected when expired, or when the security stamp changed (password change, sign out everywhere).
        if (ticket?.Properties.ExpiresUtc is not { } expires
            || timeProvider.GetUtcNow() >= expires
            || await signInManager.ValidateSecurityStampAsync(ticket.Principal) is not { } user
            || !await signInManager.CanSignInAsync(user)
            || await signInManager.UserManager.IsLockedOutAsync(user))
        {
            return TypedResults.Problem("Sign in again.", statusCode: StatusCodes.Status401Unauthorized);
        }

        var principal = await signInManager.CreateUserPrincipalAsync(user);
        return TypedResults.SignIn(principal, authenticationScheme: IdentityConstants.BearerScheme);
    }

    private static async Task<IResult> RedeemMobileCode(
        MobileCodeRequest request, MobileCodes codes, SignInManager<AppUser> signInManager)
    {
        var user = await codes.RedeemAsync(request.Code, request.CodeVerifier, signInManager.UserManager);
        if (user is null || !await signInManager.CanSignInAsync(user) || await signInManager.UserManager.IsLockedOutAsync(user))
        {
            return TypedResults.Problem("The sign-in expired or wasn't valid. Try again.", statusCode: StatusCodes.Status401Unauthorized);
        }

        var principal = await signInManager.CreateUserPrincipalAsync(user);
        return TypedResults.SignIn(principal, authenticationScheme: IdentityConstants.BearerScheme);
    }

    /// <summary>Form posts are checked for the antiforgery token automatically.</summary>
    private static async Task<IResult> ConfirmMobileSignIn(
        HttpContext context, [FromForm] string challenge, MobileCodes codes, UserManager<AppUser> userManager)
    {
        // Signed in on the normal login pages (password, two-factor or passkey) with the session cookie.
        var cookie = await context.AuthenticateAsync(IdentityConstants.ApplicationScheme);
        var user = cookie.Succeeded ? await userManager.GetUserAsync(cookie.Principal) : null;
        if (user is null || !MobileCodes.IsValidChallenge(challenge))
        {
            return TypedResults.Redirect($"{context.Request.PathBase}/{MobileSignIn.StartPath}?challenge={Uri.EscapeDataString(challenge ?? "")}");
        }

        // A fixed destination: the app's own callback, never an address from the request.
        var code = await codes.IssueAsync(user, challenge, userManager);
        return TypedResults.Redirect($"{MobileSignIn.CallbackUri}?code={Uri.EscapeDataString(code)}");
    }

    private static ProblemHttpResult LoginProblem(string type, string detail) =>
        TypedResults.Problem(detail, statusCode: StatusCodes.Status401Unauthorized, title: "Sign-in failed", type: type);
}
