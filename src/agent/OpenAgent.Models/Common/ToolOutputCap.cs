namespace OpenAgent.Models.Common;

/// <summary>
/// Caps oversized tool output before it enters conversation history and the LLM context.
/// A single unbounded tool result (e.g. an unfiltered API dump or reading a huge file) can
/// exceed the model's context window and wedge the conversation — compaction cannot recover
/// because summarizing the cut messages re-feeds the same oversized blob to the summarizer.
/// Truncating with a notice keeps the agent functional and signals it to narrow the request.
/// </summary>
public static class ToolOutputCap
{
    /// <summary>
    /// Default maximum characters for a single tool result (~25k tokens of headroom under a
    /// 200k-token context window). A single tool call should never dominate the context.
    /// </summary>
    public const int DefaultMaxChars = 100_000;

    /// <summary>
    /// Returns <paramref name="output"/> unchanged when it is within <paramref name="maxChars"/>,
    /// otherwise the first <paramref name="maxChars"/> characters followed by a truncation notice
    /// stating the original size so the agent knows to narrow its command or query.
    /// </summary>
    public static string Apply(string output, int maxChars = DefaultMaxChars)
    {
        if (output is null || output.Length <= maxChars)
            return output;

        var omitted = output.Length - maxChars;
        return string.Concat(
            output.AsSpan(0, maxChars),
            $"\n\n[output truncated: {output.Length} characters returned, capped at {maxChars}; " +
            $"{omitted} characters omitted. Narrow the command or query (filters, date ranges, " +
            $"head/limit) to get the full result.]");
    }
}
