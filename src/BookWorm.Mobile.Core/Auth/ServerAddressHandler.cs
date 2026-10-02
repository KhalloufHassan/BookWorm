namespace BookWorm.Mobile.Core.Auth;

/// <summary>
/// Sends requests to the server the user chose. The app's HttpClients are created before that's known
/// (and it can change), so they use <see cref="Placeholder"/> as their base address and this handler
/// swaps it for the real one.
/// </summary>
public sealed class ServerAddressHandler(SessionManager session) : DelegatingHandler
{
    public static readonly Uri Placeholder = new("https://bookworm.server/");

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.RequestUri is { } uri && uri.Host == Placeholder.Host)
        {
            var server = session.ServerAddress ?? throw new HttpRequestException("No server has been chosen yet.");
            request.RequestUri = new Uri(new Uri(server), uri.PathAndQuery.TrimStart('/'));
        }

        return base.SendAsync(request, cancellationToken);
    }
}
