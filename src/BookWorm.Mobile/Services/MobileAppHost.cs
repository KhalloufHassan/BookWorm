using BookWorm.Mobile.Core.Auth;
using BookWorm.Mobile.Core.Updates;
using BookWorm.UI.Services;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Devices;

namespace BookWorm.Mobile.Services;

/// <summary>The mobile app's side of <see cref="IAppHost"/>: direct API access with the token, lifecycle, reading mode.</summary>
public sealed class MobileAppHost(SessionManager session) : IAppHost
{
    /// <summary>Where the app is released (GitHub releases tagged android-vX.Y.Z).</summary>
    public const string Repository = "KhalloufHassan/BookWorm";

    public bool IsNativeApp => true;

    public string AppVersion => AppInfo.Current.VersionString;

    public bool UsesPageCloseBeacon => false;

    public event Func<Task> Pausing;

    public async ValueTask<ApiAccess> GetApiAccessAsync()
    {
        var token = await session.GetAccessTokenAsync();
        return new ApiAccess(session.ServerAddress, token is null ? null : $"Bearer {token}");
    }

    public async Task<AppRelease> FindNewerReleaseAsync(CancellationToken cancellationToken)
    {
        using var http = new HttpClient();
        return await new GitHubReleases(http, Repository).FindNewerAsync(AppVersion, cancellationToken);
    }

    public void SetReading(bool reading) => MainThread.BeginInvokeOnMainThread(() =>
    {
        DeviceDisplay.Current.KeepScreenOn = reading;
#if ANDROID
        if (Platform.CurrentActivity?.Window is { } window)
        {
            var controller = AndroidX.Core.View.WindowCompat.GetInsetsController(window, window.DecorView);
            if (reading)
            {
                controller.SystemBarsBehavior = AndroidX.Core.View.WindowInsetsControllerCompat.BehaviorShowTransientBarsBySwipe;
                controller.Hide(AndroidX.Core.View.WindowInsetsCompat.Type.SystemBars());
            }
            else
            {
                controller.Show(AndroidX.Core.View.WindowInsetsCompat.Type.SystemBars());
            }
        }
#endif
    });

    /// <summary>Called when Android pauses the app: pages save what they have.</summary>
    public async Task OnPauseAsync()
    {
        if (Pausing is { } handlers)
        {
            foreach (var handler in handlers.GetInvocationList().Cast<Func<Task>>())
            {
                try
                {
                    await handler();
                }
                catch (HttpRequestException)
                {
                    // Saved offline or next time.
                }
            }
        }
    }
}
