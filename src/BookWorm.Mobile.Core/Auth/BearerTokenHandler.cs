using System.Net;
using System.Net.Http.Headers;

namespace BookWorm.Mobile.Core.Auth;

/// <summary>
/// Adds the app's access token to API requests. When the server still answers 401 (e.g. the token
/// was revoked early), it refreshes once and sends the request again.
/// </summary>
public sealed class BearerTokenHandler(SessionManager session) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var token = await session.GetAccessTokenAsync(cancellationToken: cancellationToken);
        if (token is null)
        {
            return await base.SendAsync(request, cancellationToken);
        }

        // The app's own requests carry small JSON bodies; keep a copy in case the request is sent again.
        var body = request.Content is null ? null : await request.Content.ReadAsByteArrayAsync(cancellationToken);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var response = await base.SendAsync(request, cancellationToken);
        if (response.StatusCode != HttpStatusCode.Unauthorized)
        {
            return response;
        }

        var refreshed = await session.GetAccessTokenAsync(force: true, cancellationToken);
        if (refreshed is null || refreshed == token)
        {
            return response;
        }

        response.Dispose();
        var retry = Copy(request, body);
        retry.Headers.Authorization = new AuthenticationHeaderValue("Bearer", refreshed);
        return await base.SendAsync(retry, cancellationToken);
    }

    private static HttpRequestMessage Copy(HttpRequestMessage request, byte[] body)
    {
        var copy = new HttpRequestMessage(request.Method, request.RequestUri) { Version = request.Version };
        foreach (var (name, values) in request.Headers)
        {
            copy.Headers.TryAddWithoutValidation(name, values);
        }

        if (body is not null)
        {
            copy.Content = new ByteArrayContent(body);
            foreach (var (name, values) in request.Content.Headers)
            {
                copy.Content.Headers.TryAddWithoutValidation(name, values);
            }
        }

        return copy;
    }
}
