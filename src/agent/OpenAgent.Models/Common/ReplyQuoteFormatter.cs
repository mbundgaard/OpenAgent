using System.Text.RegularExpressions;
using OpenAgent.Models.Conversations;

namespace OpenAgent.Models.Common;

/// <summary>
/// Renders a user (or assistant) message that is a reply to an earlier channel message
/// as a <c>&lt;replying_to&gt;</c> XML block followed by the actual message content. The LLM
/// sees the quoted text inline (with author + timestamp metadata) and can disambiguate
/// which earlier message is being replied to. Output is never persisted — this runs at
/// LLM-context-build time only.
/// </summary>
public static class ReplyQuoteFormatter
{
    private const int MaxQuotedLength = 200;
    private static readonly Regex WhitespaceRun = new(@"\s+", RegexOptions.Compiled);

    /// <summary>
    /// Formats a reply with an XML-tagged quote block. When <paramref name="quotedMessage"/>
    /// is null, has no content, or has whitespace-only content, returns
    /// <paramref name="content"/> unchanged (no quote available — e.g. the replied-to
    /// message was compacted out of context or never had text).
    /// </summary>
    /// <param name="content">The actual message content the user typed.</param>
    /// <param name="quotedMessage">The full replied-to Message (for role + timestamp), or null if unavailable.</param>
    /// <returns>The content prefixed with a <c>&lt;replying_to&gt;</c> block, or unchanged if no quote.</returns>
    public static string Format(string? content, Message? quotedMessage)
    {
        if (quotedMessage is null || string.IsNullOrEmpty(quotedMessage.Content))
            return content ?? "";

        // Collapse all whitespace runs (newlines, tabs, multiple spaces) to a single space, trim.
        var collapsed = WhitespaceRun.Replace(quotedMessage.Content, " ").Trim();

        // Whitespace-only input collapses to empty — treat as no-quote.
        if (collapsed.Length == 0)
            return content ?? "";

        // Truncate to MaxQuotedLength, append ellipsis if cut.
        var quoted = collapsed.Length > MaxQuotedLength
            ? collapsed[..MaxQuotedLength] + "…"
            : collapsed;

        // ISO 8601 with offset, second precision — compact and unambiguous for the LLM.
        var timestamp = quotedMessage.CreatedAt.ToString("yyyy-MM-ddTHH:mm:sszzz");

        return $"<replying_to author=\"{quotedMessage.Role}\" timestamp=\"{timestamp}\">\n{quoted}\n</replying_to>\n\n{content ?? ""}";
    }
}
