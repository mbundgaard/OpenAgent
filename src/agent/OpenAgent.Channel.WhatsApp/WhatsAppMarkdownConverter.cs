using System.Text;
using Markdig;
using Markdig.Extensions.EmphasisExtras;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace OpenAgent.Channel.WhatsApp;

/// <summary>
/// Converts standard markdown to WhatsApp-compatible formatting.
/// Uses Markdig to parse markdown into an AST, then walks the tree
/// to produce WhatsApp-supported text formatting.
/// </summary>
public static class WhatsAppMarkdownConverter
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseEmphasisExtras(EmphasisExtraOptions.Strikethrough)
        .Build();

    /// <summary>
    /// Converts markdown text to WhatsApp-compatible formatted text.
    /// Falls back to plain text on any parse error.
    /// </summary>
    public static string ToWhatsApp(string markdown)
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
            // Fallback: plain text
            return markdown;
        }
    }

    /// <summary>
    /// Splits text into chunks that fit within WhatsApp's message size limit.
    /// Splits at paragraph boundaries first, then newlines, then hard cuts.
    /// </summary>
    public static List<string> ChunkText(string text, int maxLength)
    {
        if (string.IsNullOrEmpty(text))
            return [string.Empty];

        if (text.Length <= maxLength)
            return [text];

        var chunks = new List<string>();
        var remaining = text;

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
    /// Renders a single block element to WhatsApp formatting.
    /// </summary>
    private static void RenderBlock(StringBuilder sb, Block block)
    {
        switch (block)
        {
            case HeadingBlock heading:
                // WhatsApp has no heading syntax -- render as bold
                sb.Append('*');
                RenderInlines(sb, heading.Inline);
                sb.Append('*');
                sb.Append('\n');
                break;

            case ParagraphBlock paragraph:
                RenderInlines(sb, paragraph.Inline);
                sb.Append('\n');
                break;

            case FencedCodeBlock fencedCode:
                sb.Append("```\n");
                // Render code lines
                var lines = fencedCode.Lines;
                for (var i = 0; i < lines.Count; i++)
                {
                    var line = lines.Lines[i];
                    if (i > 0)
                        sb.Append('\n');
                    sb.Append(line.Slice.ToString());
                }
                sb.Append("\n```");
                sb.Append('\n');
                break;

            case CodeBlock codeBlock:
                sb.Append("```\n");
                var codeLines = codeBlock.Lines;
                for (var i = 0; i < codeLines.Count; i++)
                {
                    var line = codeLines.Lines[i];
                    if (i > 0)
                        sb.Append('\n');
                    sb.Append(line.Slice.ToString());
                }
                sb.Append("\n```");
                sb.Append('\n');
                break;

            case QuoteBlock quoteBlock:
                // WhatsApp doesn't have blockquote -- prefix with >
                var quoteBuilder = new StringBuilder();
                RenderBlocks(quoteBuilder, quoteBlock);
                foreach (var quoteLine in quoteBuilder.ToString().TrimEnd('\n').Split('\n'))
                {
                    sb.Append("> ");
                    sb.Append(quoteLine);
                    sb.Append('\n');
                }
                break;

            case ListBlock listBlock:
                RenderList(sb, listBlock);
                break;

            case ThematicBreakBlock:
                sb.Append("---\n");
                break;

            default:
                // Unknown blocks -- try to render as container or ignore
                if (block is ContainerBlock containerBlock)
                    RenderBlocks(sb, containerBlock);
                break;
        }
    }

    /// <summary>
    /// Renders ordered/unordered lists as indented text.
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
    /// Renders a single inline element to WhatsApp formatting.
    /// </summary>
    private static void RenderInline(StringBuilder sb, Inline inline)
    {
        switch (inline)
        {
            case LiteralInline literal:
                sb.Append(literal.Content.ToString());
                break;

            case EmphasisInline emphasis:
                var (openTag, closeTag) = GetEmphasisTags(emphasis);
                sb.Append(openTag);
                RenderInlines(sb, emphasis);
                sb.Append(closeTag);
                break;

            case CodeInline code:
                sb.Append('`');
                sb.Append(code.Content);
                sb.Append('`');
                break;

            case LinkInline link:
                RenderLink(sb, link);
                break;

            case LineBreakInline:
                sb.Append('\n');
                break;

            case HtmlInline htmlInline:
                sb.Append(htmlInline.Tag);
                break;

            case ContainerInline container:
                // Generic container -- render children
                RenderInlines(sb, container);
                break;
        }
    }

    /// <summary>
    /// Returns the open/close tags for an emphasis inline.
    /// ** -> *text* (bold), ~~ -> ~text~ (strikethrough), * -> _text_ (italic).
    /// </summary>
    private static (string Open, string Close) GetEmphasisTags(EmphasisInline emphasis)
    {
        if (emphasis.DelimiterChar == '~')
            return ("~", "~");

        if (emphasis.DelimiterCount == 2)
            return ("*", "*");

        return ("_", "_");
    }

    /// <summary>
    /// Renders a link as "text (url)" for WhatsApp.
    /// </summary>
    private static void RenderLink(StringBuilder sb, LinkInline link)
    {
        var url = link.Url ?? string.Empty;

        // Render link text
        RenderInlines(sb, link);

        // Append URL if present and different from text
        if (!string.IsNullOrEmpty(url))
        {
            sb.Append(" (");
            sb.Append(url);
            sb.Append(')');
        }
    }
}
