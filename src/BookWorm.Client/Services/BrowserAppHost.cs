using BookWorm.UI.Services;

namespace BookWorm.Client.Services;

/// <summary>The web app: the API is on the page's own origin and the browser sends the session cookie.</summary>
internal sealed class BrowserAppHost : IAppHost
{
    private static readonly ApiAccess SameOrigin = new("", null);

    public bool IsNativeApp => false;

    public string AppVersion => null;

    public bool UsesPageCloseBeacon => true;

    public Task<AppRelease> FindNewerReleaseAsync(CancellationToken cancellationToken) => Task.FromResult<AppRelease>(null);

    public ValueTask<ApiAccess> GetApiAccessAsync() => ValueTask.FromResult(SameOrigin);

    public void SetReading(bool reading)
    {
    }

    // Browsers report going to the background to the reader directly (visibilitychange).
    public event Func<Task> Pausing { add { } remove { } }
}
