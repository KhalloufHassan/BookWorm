using BookWorm.Mobile.Core.WebView;
using Microsoft.Maui.Controls;

namespace BookWorm.Mobile.Services;

/// <summary>
/// Answers the web view's API requests (book files and covers) through <see cref="WebViewFiles"/>,
/// using .NET MAUI's web request interception.
/// </summary>
public sealed class WebViewRequests(WebViewFiles files)
{
    public void OnWebResourceRequested(object sender, WebViewWebResourceRequestedEventArgs e)
    {
        if (!WebViewFiles.Handles(e.Method, e.Uri))
        {
            return;
        }

        e.Handled = true;
        var local = files.FindDownloaded(e.Uri);
        var headers = new Dictionary<string, string>
        {
            ["Content-Type"] = local?.ContentType ?? WebViewFiles.GuessContentType(e.Uri),
            ["Cache-Control"] = "no-store",
        };

        // Answered right away; a file from the server streams in when it arrives, without holding up the app.
        if (local is not null)
        {
            e.SetResponse(200, "OK", headers, local.Body);
        }
        else
        {
            e.SetResponse(200, "OK", headers, files.FetchAsync(e.Uri, CancellationToken.None));
        }
    }
}
