using System.Net;
using System.Text;
using Markdig;
using Markdig.Extensions.EmphasisExtras;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace OpenAgent.Channel.Telegram;

/// <summary>
/// Converts standard markdown to Telegram-compatible HTML.
/// Uses Markdig to parse markdown into an AST, then walks the tree
/// to produce only the HTML tags Telegram supports.
/// </summary>
public static class TelegramMarkdownConverter
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseEmphasisExtras(EmphasisExtraOptions.Strikethrough)
        .Build();

    /// <summary>
    /// Converts markdown text to Telegram-compatible HTML.
    /// Falls back to HTML-escaped plain text on any parse error.
    /// </summary>
    public static string ToTelegramHtml(string markdown)
    {
        if (string.IsNullOrEmpty(markdown))
            return string.Empty;

        try
        {
            var document = Markdown.Parse(markdown, Pipeline);
            var sb = new StringBuilder();
            RenderBlocks(sb, document);
            return sb.ToString().Trim();
        }
        catch
        {
            // Fallback: HTML-escaped plain text
            return WebUtility.HtmlEncode(markdown);
        }
    }

    /// <summary>
    /// Splits markdown into chunks that fit within Telegram's message size limit.
    /// Splits at paragraph boundaries first, then newlines, then hard cuts.
    /// Each chunk is returned as raw markdown (not yet converted to HTML).
    /// </summary>
    public static List<string> ChunkMarkdown(string markdown, int maxLength)
    {
        if (string.IsNullOrEmpty(markdown))
            return [string.Empty];

        if (markdown.Length <= maxLength)
            return [markdown];

        var chunks = new List<string>();
        var remaining = markdown;

        while (remaining.Length > 0)
        {
            if (remaining.Length <= maxLength)
            {
                chunks.Add(remaining);
                break;
            }

            // Try to split at paragraph boundary (\n\n) within maxLength
            var cutIndex = FindLastIndexOf(remaining, "\n\n", maxLength);
            if (cutIndex > 0)
            {
                chunks.Add(remaining[..cutIndex]);
                remaining = remaining[(cutIndex + 2)..]; // skip \n\n
                continue;
            }

            // Try to split at single newline within maxLength
            cutIndex = remaining.LastIndexOf('\n', maxLength - 1);
            if (cutIndex > 0)
            {
                chunks.Add(remaining[..cutIndex]);
                remaining = remaining[(cutIndex + 1)..]; // skip \n
                continue;
            }

            // Hard cut at maxLength
            chunks.Add(remaining[..maxLength]);
            remaining = remaining[maxLength..];
        }

        return chunks;
    }

    /// <summary>
    /// Finds the last occurrence of a substring within the first maxLength characters.
    /// </summary>
    private static int FindLastIndexOf(string text, string value, int maxLength)
    {
        var searchRange = text[..Math.Min(text.Length, maxLength)];
        return searchRange.LastIndexOf(value, StringComparison.Ordinal);
    }

    /// <summary>
    /// Renders all blocks in a container (document or blockquote).
    /// </summary>
    private static void RenderBlocks(StringBuilder sb, ContainerBlock container)
    {
        var isFirst = true;
        foreach (var block in container)
        {
            // Separate blocks with newlines (except the first)
            if (!isFirst && block is not FencedCodeBlock and not CodeBlock)
                sb.Append('\n');
            isFirst = false;

            RenderBlock(sb, block);
        }
    }

    /// <summary>
    /// Renders a single block element to Telegram HTML.
    /// </summary>
    private static void RenderBlock(StringBuilder sb, Block block)
    {
        switch (block)
        {
            case HeadingBlock heading:
                // Telegram has no heading tags — render as bold
                sb.Append("<b>");
                RenderInlines(sb, heading.Inline);
                sb.Append("</b>");
                sb.Append('\n');
                break;

            case ParagraphBlock paragraph:
                RenderInlines(sb, paragraph.Inline);
                sb.Append('\n');
                break;

            case FencedCodeBlock fencedCode:
                var language = fencedCode.Info;
                if (!string.IsNullOrWhiteSpace(language))
                    sb.Append($"<pre><code class=\"language-{WebUtility.HtmlEncode(language)}\">");
                else
                    sb.Append("<pre><code>");

                // Render code lines
                var lines = fencedCode.Lines;
                for (var i = 0; i < lines.Count; i++)
                {
                    var line = lines.Lines[i];
                    if (i > 0)
                        sb.Append('\n');
                    sb.Append(WebUtility.HtmlEncode(line.Slice.ToString()));
                }

                sb.Append("</code></pre>");
                sb.Append('\n');
                break;

            case CodeBlock codeBlock:
                sb.Append("<pre><code>");
                var codeLines = codeBlock.Lines;
                for (var i = 0; i < codeLines.Count; i++)
                {
                    var line = codeLines.Lines[i];
                    if (i > 0)
                        sb.Append('\n');
                    sb.Append(WebUtility.HtmlEncode(line.Slice.ToString()));
                }
                sb.Append("</code></pre>");
                sb.Append('\n');
                break;

            case QuoteBlock quoteBlock:
                sb.Append("<blockquote>");
                RenderBlocks(sb, quoteBlock);
                sb.Append("</blockquote>");
                sb.Append('\n');
                break;

            case ListBlock listBlock:
                RenderList(sb, listBlock);
                break;

            case ThematicBreakBlock:
                sb.Append("---\n");
                break;

            default:
                // Unknown blocks — try to render as container or ignore
                if (block is ContainerBlock containerBlock)
                    RenderBlocks(sb, containerBlock);
                break;
        }
    }

    /// <summary>
    /// Renders ordered/unordered lists as indented text (Telegram has no list tags).
    /// </summary>
    private static void RenderList(StringBuilder sb, ListBlock listBlock)
    {
        var index = 1;
        foreach (var item in listBlock)
        {
            if (item is not ListItemBlock listItem) continue;

            var bullet = listBlock.IsOrdered ? $"{index}. " : "- ";
            sb.Append(bullet);
            index++;

            // Render list item content inline
            foreach (var subBlock in listItem)
            {
                if (subBlock is ParagraphBlock para)
                    RenderInlines(sb, para.Inline);
                else
                    RenderBlock(sb, subBlock);
            }

            sb.Append('\n');
        }
    }

    /// <summary>
    /// Renders all inline elements within a container inline.
    /// </summary>
    private static void RenderInlines(StringBuilder sb, ContainerInline? container)
    {
        if (container == null) return;

        foreach (var inline in container)
        {
            RenderInline(sb, inline);
        }
    }

    /// <summary>
    /// Renders a single inline element to Telegram HTML.
    /// </summary>
    private static void RenderInline(StringBuilder sb, Inline inline)
    {
        switch (inline)
        {
            case LiteralInline literal:
                sb.Append(WebUtility.HtmlEncode(literal.Content.ToString()));
                break;

            case EmphasisInline emphasis:
                var (openTag, closeTag) = GetEmphasisTags(emphasis);
                sb.Append(openTag);
                RenderInlines(sb, emphasis);
                sb.Append(closeTag);
                break;

            case CodeInline code:
                sb.Append("<code>");
                sb.Append(WebUtility.HtmlEncode(code.Content));
                sb.Append("</code>");
                break;

            case LinkInline link:
                RenderLink(sb, link);
                break;

            case LineBreakInline:
                sb.Append('\n');
                break;

            case HtmlInline htmlInline:
                // Escape raw HTML — don't pass it through
                sb.Append(WebUtility.HtmlEncode(htmlInline.Tag));
                break;

            case ContainerInline container:
                // Generic container — render children
                RenderInlines(sb, container);
                break;
        }
    }

    /// <summary>
    /// Returns the open/close HTML tags for an emphasis inline.
    /// ** → bold, ~~ → strikethrough, * → italic.
    /// </summary>
    private static (string Open, string Close) GetEmphasisTags(EmphasisInline emphasis)
    {
        if (emphasis.DelimiterChar == '~')
            return ("<s>", "</s>");

        if (emphasis.DelimiterCount == 2)
            return ("<b>", "</b>");

        return ("<i>", "</i>");
    }

    /// <summary>
    /// Renders a link. Only http/https schemes produce anchor tags;
    /// other schemes (javascript:, data:, etc.) render text only.
    /// </summary>
    private static void RenderLink(StringBuilder sb, LinkInline link)
    {
        var url = link.Url ?? string.Empty;

        // Only allow http and https schemes
        var isSafe = url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                     || url.StartsWith("http://", StringComparison.OrdinalIgnoreCase);

        if (isSafe)
        {
            sb.Append($"<a href=\"{WebUtility.HtmlEncode(url)}\">");
            RenderInlines(sb, link);
            sb.Append("</a>");
        }
        else
        {
            // Unsafe scheme — render link text only
            RenderInlines(sb, link);
        }
    }
}
