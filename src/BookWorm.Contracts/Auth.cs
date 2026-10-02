using System.ComponentModel.DataAnnotations;

namespace BookWorm.Contracts;

/// <summary>
/// Signs an app in with a user name and password. When the account has two-factor authentication,
/// the first attempt fails with <see cref="AuthProblemTypes.TwoFactorRequired"/>; the app then asks
/// for a code and sends everything again with <see cref="TwoFactorCode"/> or <see cref="RecoveryCode"/>.
/// </summary>
public sealed class AppLoginRequest
{
    [Required]
    public string UserName { get; set; } = "";

    [Required]
    public string Password { get; set; } = "";

    /// <summary>The 6-digit code from the authenticator app.</summary>
    public string TwoFactorCode { get; set; }

    public string RecoveryCode { get; set; }
}

public sealed class RefreshTokenRequest
{
    [Required]
    public string RefreshToken { get; set; } = "";
}

/// <summary>
/// Exchanges the one-time code from the browser sign-in (used for passkeys) for tokens. The app made
/// <see cref="CodeVerifier"/> itself and only sent its SHA-256 hash to the browser (PKCE), so a code
/// intercepted on its way back to the app is useless on its own.
/// </summary>
public sealed class MobileCodeRequest
{
    [Required]
    public string Code { get; set; } = "";

    [Required]
    [StringLength(128, MinimumLength = 43)]
    public string CodeVerifier { get; set; } = "";
}

/// <summary>Tokens for an app. Send the access token as <c>Authorization: Bearer …</c>.</summary>
/// <param name="ExpiresIn">Seconds until the access token expires; get new tokens with the refresh token before then.</param>
public sealed record AppTokens(string TokenType, string AccessToken, long ExpiresIn, string RefreshToken);

/// <summary>The <see cref="ApiProblem.Type"/> of failed app sign-ins, so the app can react to each.</summary>
public static class AuthProblemTypes
{
    public const string TwoFactorRequired = "urn:bookworm:auth:two-factor-required";
    public const string InvalidTwoFactorCode = "urn:bookworm:auth:invalid-two-factor-code";
    public const string LockedOut = "urn:bookworm:auth:locked-out";
    public const string InvalidCredentials = "urn:bookworm:auth:invalid-credentials";
}

/// <summary>The browser sign-in used by the mobile app, so passkeys work there too.</summary>
public static class MobileSignIn
{
    /// <summary>Opened in the system browser with <c>?challenge=</c> (the PKCE code challenge).</summary>
    public const string StartPath = "Account/MobileLogin";

    /// <summary>Where the browser sends the one-time code: <c>bookworm://auth?code=…</c>.</summary>
    public const string CallbackUri = "bookworm://auth";
}
