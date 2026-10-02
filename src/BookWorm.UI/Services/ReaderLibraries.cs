using System.Reflection;

namespace BookWorm.UI.Services;

/// <summary>
/// The reader's JavaScript libraries. The reader imports them by bare name (<c>pdfjs-dist/…</c>,
/// <c>foliate-js/…</c>), and each host maps those names with an import map: the web app to jsDelivr,
/// the mobile app to copies bundled inside it (for offline reading). Their versions are set once, in
/// Directory.Build.props.
/// </summary>
public static class ReaderLibraries
{
    public static string PdfJsVersion { get; } = Metadata("PdfJsVersion");

    /// <summary>foliate-js has no releases, so it is pinned to a commit.</summary>
    public static string FoliateJsCommit { get; } = Metadata("FoliateJsCommit");

    /// <summary>Import map entries for loading the libraries from jsDelivr.</summary>
    public static IReadOnlyDictionary<string, string> CdnImports { get; } = new Dictionary<string, string>
    {
        ["pdfjs-dist/"] = $"https://cdn.jsdelivr.net/npm/pdfjs-dist@{PdfJsVersion}/",
        ["foliate-js/"] = $"https://cdn.jsdelivr.net/gh/johnfactotum/foliate-js@{FoliateJsCommit}/",
    };

    private static string Metadata(string key) =>
        typeof(ReaderLibraries).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(a => a.Key == key).Value;
}
