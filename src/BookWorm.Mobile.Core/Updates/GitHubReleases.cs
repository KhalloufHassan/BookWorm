using System.Net.Http.Json;
using System.Text.Json.Serialization;
using BookWorm.UI.Services;

namespace BookWorm.Mobile.Core.Updates;

/// <summary>
/// Finds newer releases of the app on GitHub. App releases are tagged <c>android-v1.2.3</c>, so other
/// releases in the same repository (e.g. of the server) are ignored.
/// </summary>
public sealed class GitHubReleases(HttpClient http, string repository, string tagPrefix = "android-v")
{
    public async Task<AppRelease> FindNewerAsync(string currentVersion, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{repository}/releases?per_page=30");
        request.Headers.UserAgent.ParseAdd("BookWorm-App");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var response = await http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        var releases = await response.Content.ReadFromJsonAsync<List<Release>>(cancellationToken) ?? [];
        return Newest(releases, currentVersion);
    }

    internal AppRelease Newest(IEnumerable<Release> releases, string currentVersion)
    {
        Version.TryParse(currentVersion, out var current);
        return releases
            .Where(release => !release.Draft && !release.Prerelease && release.TagName?.StartsWith(tagPrefix, StringComparison.OrdinalIgnoreCase) == true)
            .Select(release => (Release: release, Version: Version.TryParse(release.TagName[tagPrefix.Length..], out var version) ? version : null))
            .Where(candidate => candidate.Version is not null && (current is null || candidate.Version > current))
            .OrderByDescending(candidate => candidate.Version)
            .Select(candidate => new AppRelease(candidate.Version.ToString(), candidate.Release.HtmlUrl))
            .FirstOrDefault();
    }

    internal sealed record Release(
        [property: JsonPropertyName("tag_name")] string TagName,
        [property: JsonPropertyName("html_url")] string HtmlUrl,
        [property: JsonPropertyName("draft")] bool Draft,
        [property: JsonPropertyName("prerelease")] bool Prerelease);
}
