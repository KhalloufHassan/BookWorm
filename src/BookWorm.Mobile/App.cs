using BookWorm.Mobile.Core.Offline;

namespace BookWorm.Mobile;

public sealed class App : Application
{
    private readonly MainPage _page;

    public App(MainPage page, SyncService sync, BookWorm.Mobile.Core.Offline.IConnectivity connectivity)
    {
        _page = page;

        // Changes made offline go out as soon as the connection is back.
        connectivity.Changed += () =>
        {
            if (connectivity.IsOnline)
            {
                _ = sync.SyncAsync();
            }
        };
        _ = sync.SyncAsync();
    }

    protected override Window CreateWindow(IActivationState activationState) => new(_page) { Title = "BookWorm" };
}
