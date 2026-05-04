namespace OpenAgent.Models.Common;

/// <summary>
/// Wraps user message content with a <c>&lt;from id="..."&gt;</c> XML tag so the LLM can
/// disambiguate speakers in group chats. Output is never persisted — this runs at
/// LLM-context-build time only, fed by the <see cref="OpenAgent.Models.Conversations.Message.Sender"/>
/// column populated by inbound channel handlers.
/// </summary>
public static class FromTagFormatter
{
    /// <summary>
    /// Returns <paramref name="content"/> wrapped as
    /// <c>&lt;from id="{sender}"&gt;{content}&lt;/from&gt;</c> when <paramref name="sender"/>
    /// is non-empty, otherwise returns <paramref name="content"/> unchanged. DMs and
    /// channels without per-user identity (REST, webhook, scheduled tasks) leave
    /// <c>sender</c> null and skip the wrapper.
    /// </summary>
    public static string Wrap(string? sender, string? content)
    {
        if (string.IsNullOrEmpty(sender))
            return content ?? "";
        return $"<from id=\"{sender}\">{content ?? ""}</from>";
    }
}
