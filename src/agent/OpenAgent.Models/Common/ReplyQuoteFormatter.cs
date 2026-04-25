using System.Text.RegularExpressions;

namespace OpenAgent.Models.Common;

/// <summary>
/// Renders a user (or assistant) message that is a reply to an earlier channel message
/// as a markdown blockquote prefix followed by the actual message content. The LLM sees
/// the quoted text inline and can disambiguate which earlier message is being replied to.
/// Output is never persisted — this runs at LLM-context-build time only.
/// </summary>
public static class ReplyQuoteFormatter
{
    private const int MaxQuotedLength = 200;
    private static readonly Regex WhitespaceRun = new(@"\s+", RegexOptions.Compiled);

    /// <summary>
    /// Formats a reply with a quoted prefix. When <paramref name="quotedContent"/> is null
    /// or empty, returns <paramref name="content"/> unchanged (no quote available, e.g. the
    /// replied-to message was compacted out of context).
    /// </summary>
    /// <param name="content">The actual message content the user typed.</param>
    /// <param name="quotedContent">The replied-to message content, or null if unavailable.</param>
    /// <returns>The content prefixed with a blockquote line, or unchanged if no quote.</returns>
    public static string Format(string? content, string? quotedContent)
    {
        if (string.IsNullOrEmpty(quotedContent))
            return content ?? "";

        // Collapse all whitespace runs (newlines, tabs, multiple spaces) to a single space, trim.
        var collapsed = WhitespaceRun.Replace(quotedContent, " ").Trim();

        // Whitespace-only input collapses to empty — treat as no-quote.
        if (collapsed.Length == 0)
            return content ?? "";

        // Truncate to MaxQuotedLength, append ellipsis if cut.
        var quoted = collapsed.Length > MaxQuotedLength
            ? collapsed[..MaxQuotedLength] + "…"
            : collapsed;

        return $"> {quoted}\n\n{content ?? ""}";
    }
}
