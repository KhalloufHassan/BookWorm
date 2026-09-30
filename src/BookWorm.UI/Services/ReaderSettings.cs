using System.Text.Json;
using System.Text.Json.Serialization;

namespace BookWorm.UI.Services;

/// <summary>How books look in the reader. Remembered per device, like the app theme.</summary>
public sealed class ReaderSettings
{
    /// <summary>light, sepia or dark. Chosen separately from the app's own light or dark look.</summary>
    public string Theme { get; set; } = "light";

    /// <summary>Text size in percent of the book's own.</summary>
    public int FontSize { get; set; } = 100;

    /// <summary>publisher (the book's own fonts), serif or sans.</summary>
    public string FontFamily { get; set; } = "publisher";

    public double LineHeight { get; set; } = 1.5;

    /// <summary>narrow, normal or wide.</summary>
    public string Margin { get; set; } = "normal";

    /// <summary>paginated or scrolled.</summary>
    public string Flow { get; set; } = "paginated";

    public bool Justify { get; set; } = true;

    /// <summary>auto (two columns on wide screens) or one.</summary>
    public string Columns { get; set; } = "auto";

    /// <summary>PDF zoom: page-width, page-fit or auto.</summary>
    public string PdfZoom { get; set; } = "page-width";

    /// <summary>Comics and fixed-layout books: fit-page or fit-width.</summary>
    public string FixedZoom { get; set; } = "fit-page";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static async Task<ReaderSettings> LoadAsync(IPreferenceStore preferences)
    {
        try
        {
            return await preferences.GetAsync(PreferenceKeys.Reader) is { Length: > 0 } json
                ? JsonSerializer.Deserialize<ReaderSettings>(json, Json) ?? new ReaderSettings()
                : new ReaderSettings();
        }
        catch (JsonException)
        {
            return new ReaderSettings();
        }
    }

    public ValueTask SaveAsync(IPreferenceStore preferences) =>
        preferences.SetAsync(PreferenceKeys.Reader, JsonSerializer.Serialize(this, Json));

    /// <summary>The page colours actually used; anything unknown (e.g. the old "app" setting) is light.</summary>
    [JsonIgnore]
    public string EffectiveTheme => Theme is "sepia" or "dark" ? Theme : "light";

    [JsonIgnore]
    public bool IsDark => EffectiveTheme == "dark";
}
