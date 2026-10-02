namespace BookWorm.UI.Services;

/// <summary>How JavaScript reaches the API directly (uploads): relative URLs and the cookie on the web.</summary>
/// <param name="BaseUrl">Prefix for API URLs, e.g. "https://books.example.org/"; empty for the page's own origin.</param>
/// <param name="Authorization">The Authorization header to send, or null to send the session cookie instead.</param>
public sealed record ApiAccess(string BaseUrl, string Authorization);

/// <summary>A published release of the mobile app.</summary>
/// <param name="Url">The release page, where the APK can be downloaded.</param>
public sealed record AppRelease(string Version, string Url);

/// <summary>What differs between the web app and the mobile app, beyond sign-in and storage.</summary>
public interface IAppHost
{
    /// <summary>True in the mobile app.</summary>
    bool IsNativeApp { get; }

    /// <summary>The mobile app's version, or null in the web app.</summary>
    string AppVersion { get; }

    /// <summary>A newer release of the app than this one, or null when it's up to date (always null on the web).</summary>
    Task<AppRelease> FindNewerReleaseAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Whether the reader should save its position with a request sent as the page closes. The mobile
    /// app can't send those (its web view can't forward request bodies), so it raises <see cref="Pausing"/> instead.
    /// </summary>
    bool UsesPageCloseBeacon { get; }

    ValueTask<ApiAccess> GetApiAccessAsync();

    /// <summary>The reader opened or closed: the mobile app then goes full screen and keeps the screen on.</summary>
    void SetReading(bool reading);

    /// <summary>The app is going to the background: save anything unsaved now.</summary>
    event Func<Task> Pausing;
}
