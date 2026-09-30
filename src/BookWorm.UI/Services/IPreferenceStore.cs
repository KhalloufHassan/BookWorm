namespace BookWorm.UI.Services;

/// <summary>
/// Small per-device settings such as the theme or the preferred list view. The web app keeps them
/// in the browser; the mobile app will keep them on the phone.
/// </summary>
public interface IPreferenceStore
{
    ValueTask<string> GetAsync(string key);

    ValueTask SetAsync(string key, string value);
}

public static class PreferenceKeys
{
    public const string Theme = "bookworm.theme";
    public const string BooksView = "bookworm.books.view";
    public const string AuthorsView = "bookworm.authors.view";
    public const string Reader = "bookworm.reader";
}
