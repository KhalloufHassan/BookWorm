using BookWorm.Mobile.Core.Auth;
using BookWorm.Mobile.Core.Offline;
using BookWorm.Mobile.Core.WebView;
using BookWorm.Mobile.Services;
using BookWorm.UI.Api;
using BookWorm.UI.Services;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Logging;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.LifecycleEvents;
using Microsoft.Maui.Storage;
using MudBlazor.Services;

namespace BookWorm.Mobile;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder.UseMauiApp<App>();

        builder.Services.AddMauiBlazorWebView();
#if DEBUG
        builder.Services.AddBlazorWebViewDeveloperTools();
        builder.Logging.AddDebug();
#endif

        builder.Services.AddAuthorizationCore();
        builder.Services.AddCascadingAuthenticationState();
        builder.Services.AddMudServices();

        // Signing in, offline reading and syncing (BookWorm.Mobile.Core), shared by the whole app.
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<AppHttp>();
        builder.Services.AddSingleton<ISessionStorage, SecureSessionStorage>();
        builder.Services.AddSingleton<BookWorm.Mobile.Core.Offline.IConnectivity, DeviceConnectivity>();
        builder.Services.AddSingleton(services => new SessionManager(
            services.GetRequiredService<ISessionStorage>(),
            address => services.GetRequiredService<AppHttp>().CreatePlain(address),
            services.GetRequiredService<TimeProvider>(),
            AppInfo.Current.VersionString));
        builder.Services.AddSingleton(_ => new OfflineStore(FileSystem.AppDataDirectory));
        builder.Services.AddSingleton<OfflineQueue>();
        builder.Services.AddSingleton(services => new SyncService(
            services.GetRequiredService<OfflineStore>(),
            services.GetRequiredService<OfflineQueue>(),
            services.GetRequiredService<SessionManager>(),
            services.GetRequiredService<BookWorm.Mobile.Core.Offline.IConnectivity>(),
            services.GetRequiredService<AppHttp>().CreateToServer));
        builder.Services.AddSingleton(services => new OfflineLibrary(
            services.GetRequiredService<OfflineStore>(),
            services.GetRequiredService<OfflineQueue>(),
            services.GetRequiredService<SessionManager>(),
            services.GetRequiredService<BookWorm.Mobile.Core.Offline.IConnectivity>(),
            services.GetRequiredService<SyncService>(),
            services.GetRequiredService<AppHttp>().CreateToServer,
            services.GetRequiredService<TimeProvider>()));
        builder.Services.AddSingleton(services => new WebViewFiles(
            services.GetRequiredService<OfflineStore>(),
            services.GetRequiredService<SessionManager>(),
            services.GetRequiredService<BookWorm.Mobile.Core.Offline.IConnectivity>(),
            services.GetRequiredService<AppHttp>().CreateToServer));
        builder.Services.AddSingleton<WebViewRequests>();
        builder.Services.AddSingleton<MobileAppHost>();
        builder.Services.AddSingleton<MainPage>();

        // What the pages of BookWorm.UI need, as in the web app (BookWorm.Client/Program.cs).
        builder.Services.AddScoped(services => new BookWormApiClient(services.GetRequiredService<AppHttp>().CreateForPages()));
        builder.Services.AddScoped<AuthenticationStateProvider, MobileAuthenticationStateProvider>();
        builder.Services.AddScoped<IAccountService, MobileAccountService>();
        builder.Services.AddScoped<IPreferenceStore, DevicePreferenceStore>();
        builder.Services.AddSingleton<IAppHost>(services => services.GetRequiredService<MobileAppHost>());
        builder.Services.AddSingleton<IOfflineLibrary>(services => services.GetRequiredService<OfflineLibrary>());
        builder.Services.AddSingleton<IAppSignIn, MobileAppSignIn>();
        builder.Services.AddScoped<BrowseState>();
        builder.Services.AddScoped<ThemeState>();
        builder.Services.AddScoped<BrowserFiles>();
        builder.Services.AddScoped<PendingBook>();

#if ANDROID
        builder.ConfigureLifecycleEvents(events => events.AddAndroid(android => android
            .OnPause(activity => Service<MobileAppHost>()?.OnPauseAsync())
            .OnResume(activity => Service<SyncService>()?.SyncAsync())));
#endif

        return builder.Build();
    }

    private static T Service<T>() where T : class => IPlatformApplication.Current?.Services.GetService<T>();
}
