using BookWorm.Mobile.Core.Auth;
using BookWorm.Mobile.Core.Offline;

namespace BookWorm.Mobile.Services;

/// <summary>
/// Builds the app's HttpClients around one platform handler, so they share connections:
/// <list type="bullet">
/// <item>for the pages: offline answers, then the token, then the chosen server;</item>
/// <item>straight to the server (sync, downloads, files for the web view): the token and the server;</item>
/// <item>plain, for signing in and checking a server.</item>
/// </list>
/// </summary>
public sealed class AppHttp(IServiceProvider services)
{
    private readonly HttpMessageHandler _platform = new HttpClientHandler();

    public HttpClient CreatePlain(string address) => new(_platform, disposeHandler: false) { BaseAddress = new Uri(address) };

    public HttpClient CreateToServer() => new(ToServer(), disposeHandler: true)
    {
        BaseAddress = ServerAddressHandler.Placeholder,

        // Book downloads can take long.
        Timeout = Timeout.InfiniteTimeSpan,
    };

    public HttpClient CreateForPages()
    {
        var offline = new OfflineHttpHandler(
            services.GetRequiredService<OfflineStore>(),
            services.GetRequiredService<OfflineQueue>(),
            services.GetRequiredService<SessionManager>(),
            services.GetRequiredService<Core.Offline.IConnectivity>(),
            services.GetRequiredService<TimeProvider>())
        {
            InnerHandler = ToServer(),
        };
        return new HttpClient(offline) { BaseAddress = ServerAddressHandler.Placeholder };
    }

    private DelegatingHandler ToServer()
    {
        var session = services.GetRequiredService<SessionManager>();
        return new BearerTokenHandler(session)
        {
            InnerHandler = new ServerAddressHandler(session) { InnerHandler = new NonDisposingHandler(_platform) },
        };
    }

    /// <summary>Keeps the shared platform handler alive when a client and its handlers are disposed.</summary>
    private sealed class NonDisposingHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        protected override void Dispose(bool disposing)
        {
        }
    }
}
