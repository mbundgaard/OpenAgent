using ReverseMarkdown;
using SmartReader;

namespace OpenAgent.Tools.WebFetch;

/// <summary>
/// Extracts readable content from HTML using SmartReader and converts to markdown via ReverseMarkdown.
/// </summary>
public static class ContentExtractor
{
    public static ExtractionResult Extract(string html, string url, int maxChars = 50_000)
    {
        if (string.IsNullOrWhiteSpace(html))
            return new ExtractionResult("", "", 0, false);

        // SmartReader extracts main article content as HTML
        var reader = new Reader(url, html);
        var article = reader.GetArticle();

        var title = article.Title ?? "";
        var articleHtml = article.Content ?? "";

        // ReverseMarkdown converts HTML to markdown
        var converter = new Converter(new Config
        {
            GithubFlavored = true,
            RemoveComments = true,
            SmartHrefHandling = true,
            UnknownTags = Config.UnknownTagsOption.Bypass,
        });

        var markdown = converter.Convert(articleHtml).Trim();

        // Truncate if needed
        var truncated = false;
        if (markdown.Length > maxChars)
        {
            markdown = markdown[..maxChars];
            truncated = true;
        }

        return new ExtractionResult(title, markdown, markdown.Length, truncated);
    }
}

/// <summary>
/// Result of content extraction from HTML.
/// </summary>
public sealed record ExtractionResult(string Title, string Content, int CharCount, bool Truncated);
