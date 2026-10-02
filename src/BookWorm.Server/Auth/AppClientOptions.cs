namespace BookWorm.Server.Auth;

/// <summary>Settings for the mobile app's sign-in (configuration section "Apps").</summary>
public sealed class AppClientOptions
{
    public const string Section = "Apps";

    /// <summary>The CORS policy that lets the mobile app's web view call the API directly (for uploads).</summary>
    public const string CorsPolicy = "MobileApp";

    /// <summary>
    /// Origins of the mobile app's web view: https://0.0.0.1 on Android, app://0.0.0.1 on iOS. They
    /// can't be reached on the internet, so websites can't use them; the app sends a bearer token,
    /// never the session cookie.
    /// </summary>
    public string[] Origins { get; set; } = ["https://0.0.0.1", "https://0.0.0.0", "app://0.0.0.1"];

    /// <summary>How long an app's access token works. Signing out everywhere takes effect within this time.</summary>
    public int AccessTokenMinutes { get; set; } = 30;

    /// <summary>How long an app stays signed in without being used.</summary>
    public int RefreshTokenDays { get; set; } = 90;

    /// <summary>The oldest mobile app version this server accepts, e.g. "1.2.0"; empty for any.</summary>
    public string MinimumVersion { get; set; }
}
