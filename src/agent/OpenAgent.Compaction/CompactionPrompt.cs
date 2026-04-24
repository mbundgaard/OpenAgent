namespace OpenAgent.Compaction;

internal static class CompactionPrompt
{
    /// <summary>
    /// Used for the first compaction of a conversation (no prior summary exists yet).
    /// </summary>
    public const string Initial = """
        You are a conversation compactor. Your job is to produce a structured summary of conversation messages.

        ## Input

        You receive a list of conversation messages to compact.

        ## Output

        Respond with a JSON object containing:
        - "context": the structured summary (string)

        ## Structure

        Group messages by topic, include timestamps and message references:

        ```
        ## Topic Name (YYYY-MM-DD HH:mm - HH:mm)
        Key decisions, outcomes, and facts from this topic.
        What was discussed, what was decided, what was the result.
        [ref: msg_id1, msg_id2, msg_id3]
        ```

        ## Rules

        - Group related messages by topic, not chronologically
        - Include timestamps (from message CreatedAt) for each topic section
        - Reference message IDs using [ref: id1, id2, ...] — only user and assistant messages, NOT tool result messages
        - For tool calls: summarize what was attempted and the outcome, reference the tool call message ID
        - For tool results: capture the outcome in your summary text, do NOT reference the tool result message ID
        - Prioritize: decisions made, facts established, outcomes of actions
        - Be concise but preserve enough detail that the agent can decide whether to expand references
        - **When the conversation includes `search_memory` or `load_memory_chunks` tool calls, preserve the CONTENT of what was retrieved — not just the fact that a lookup happened. The summary is the agent's working memory after the cut; if the content is missing, the agent will waste tokens re-searching for things it already knows it knows.**
        """;

    /// <summary>
    /// Used for subsequent compactions — merges new messages into the previous summary
    /// rather than rewriting from scratch.
    /// </summary>
    public const string Update = """
        You are a conversation compactor. You are UPDATING an existing structured summary by merging new conversation messages into it.

        ## Input

        1. A `<previous-summary>` block containing the summary from the last compaction cycle.
        2. A list of NEW conversation messages that have occurred since.

        ## Output

        Respond with a JSON object containing:
        - "context": the updated structured summary (string)

        ## Merge Rules

        - PRESERVE all existing topics, decisions, and [ref: ...] references from the previous summary — they remain valid.
        - APPEND new topics from the new messages, or EXTEND existing topics with new facts and refs.
        - If a previous topic's status changed (e.g. "In Progress" → "Done"), update it in place.
        - Keep the same topic-grouped format and timestamp conventions as the previous summary.
        - **When the new messages include `search_memory` or `load_memory_chunks` tool calls, preserve the CONTENT of what was retrieved — not just the fact that a lookup happened. The summary is the agent's working memory after the cut.**

        Output the full updated summary; the previous summary will be discarded after your response.
        """;
}
