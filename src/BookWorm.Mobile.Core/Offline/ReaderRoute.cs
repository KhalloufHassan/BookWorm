using System.Text.RegularExpressions;

namespace BookWorm.Mobile.Core.Offline;

public enum ReaderRouteKind
{
    GetBook,
    GetHighlights,
    CreateHighlight,
    UpdateHighlight,
    DeleteHighlight,
    GetCurrentRead,
    StartRead,
    SaveProgress,
    FinishRead,
    SaveBrowsePosition,
}

/// <summary>
/// One of the API calls the reader makes, which can be answered from a downloaded book while offline.
/// </summary>
/// <param name="ItemId">The highlight, read or file in the path, if any.</param>
/// <param name="Path">The request's path and query relative to the server address (<c>api/books/…</c>).</param>
public sealed partial record ReaderRoute(ReaderRouteKind Kind, Guid BookId, Guid ItemId, string Path)
{
    /// <summary>The route of a request, or null when it isn't one of the reader's calls.</summary>
    public static ReaderRoute Parse(HttpMethod method, Uri uri)
    {
        var match = BookPath().Match(uri.AbsolutePath);
        if (!match.Success)
        {
            return null;
        }

        var bookId = Guid.Parse(match.Groups["book"].Value);
        var rest = match.Groups["rest"].Value.TrimEnd('/');
        var item = match.Groups["item"].Success ? Guid.Parse(match.Groups["item"].Value) : Guid.Empty;
        var path = uri.AbsolutePath[(match.Index + 1)..] + uri.Query;

        ReaderRouteKind? kind = (method.Method, Shape(rest)) switch
        {
            ("GET", "") => ReaderRouteKind.GetBook,
            ("GET", "/highlights") => ReaderRouteKind.GetHighlights,
            ("POST", "/highlights") => ReaderRouteKind.CreateHighlight,
            ("PUT", "/highlights/{id}") => ReaderRouteKind.UpdateHighlight,
            ("DELETE", "/highlights/{id}") => ReaderRouteKind.DeleteHighlight,
            ("GET", "/reads/current") => ReaderRouteKind.GetCurrentRead,
            ("POST", "/reads/current") => ReaderRouteKind.StartRead,
            ("PUT", "/reads/{id}/progress") => ReaderRouteKind.SaveProgress,
            ("POST", "/reads/{id}/finish") => ReaderRouteKind.FinishRead,
            ("PUT", "/files/{id}/position") => ReaderRouteKind.SaveBrowsePosition,
            _ => null,
        };

        return kind is { } found ? new ReaderRoute(found, bookId, item, path) : null;
    }

    private static string Shape(string rest) => ItemSegment().Replace(rest, "/{id}");

    [GeneratedRegex(@"/api/books/(?<book>[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})(?<rest>(/[a-z]+(/(?<item>[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})|/current)?(/[a-z]+)?)?)/?$")]
    private static partial Regex BookPath();

    [GeneratedRegex(@"/[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}")]
    private static partial Regex ItemSegment();
}
