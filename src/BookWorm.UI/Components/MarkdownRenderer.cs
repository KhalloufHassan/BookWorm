using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace BookWorm.UI.Components;

/// <summary>
/// Turns notes written in Markdown into HTML. Raw HTML in the notes is shown as text and only
/// web, mail and in-page links are kept, so notes can never run scripts.
/// </summary>
public static class MarkdownRenderer
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .DisableHtml()
        .UseEmphasisExtras()
        .UseListExtras()
        .UsePipeTables()
        .UseTaskLists()
        .UseAutoLinks()
        .UseSoftlineBreakAsHardlineBreak()
        .UseReferralLinks("noopener", "noreferrer")
        .Build();

    public static string ToHtml(string markdown)
    {
        var document = Markdown.Parse(markdown, Pipeline);

        foreach (var link in document.Descendants<LinkInline>())
        {
            if (!IsSafe(link.Url))
            {
                link.Url = "";
            }
        }

        foreach (var link in document.Descendants<AutolinkInline>())
        {
            if (!IsSafe(link.Url))
            {
                link.Url = "";
            }
        }

        return Markdown.ToHtml(document, Pipeline);
    }

    private static bool IsSafe(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return true;
        }

        var trimmed = url.Trim();
        return trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith('#');
    }
}
