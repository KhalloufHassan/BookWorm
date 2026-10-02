using BookWorm.UI.Services;

namespace BookWorm.Mobile.Core.Auth;

public static class ServerAddress
{
    /// <summary>
    /// Turns what the user typed into a server address like "https://books.example.org/" (a trailing
    /// slash, so API paths resolve under it). Only HTTPS: the app never sends passwords or tokens in the clear.
    /// </summary>
    public static string Normalize(string input)
    {
        var text = (input ?? "").Trim();
        if (text.Length == 0)
        {
            throw new AppSignInException("Enter your server's address.");
        }

        if (!text.Contains("://", StringComparison.Ordinal))
        {
            text = "https://" + text;
        }

        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.Host))
        {
            throw new AppSignInException("That doesn't look like a web address.");
        }

        if (uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new AppSignInException("The server must use HTTPS (an address starting with https://).");
        }

        var builder = new UriBuilder(uri) { Query = "", Fragment = "" };
        if (!builder.Path.EndsWith('/'))
        {
            builder.Path += "/";
        }

        return builder.Uri.ToString();
    }
}
