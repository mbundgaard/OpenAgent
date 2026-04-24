using OpenAgent.Models.Common;
using OpenAgent.Models.Conversations;

namespace OpenAgent.Contracts;

/// <summary>
/// Stateless text completion provider. Sends conversation history to an LLM and returns the response.
/// The provider calls IAgentLogic for system prompt, tools, message history, and tool execution.
/// </summary>
public interface ILlmTextProvider : IConfigurable
{
    /// <summary>
    /// Runs a completion turn. Yields CompletionEvents as they occur — text deltas,
    /// tool calls, and tool results. Works for both streaming (WebSocket) and
    /// collected (REST) transports.
    /// </summary>
    IAsyncEnumerable<CompletionEvent> CompleteAsync(Conversation conversation, Message userMessage, CancellationToken ct = default);

    /// <summary>
    /// Runs a raw completion without conversation context — no tool calls, no message
    /// persistence, no system prompt. Used by compaction and other non-conversation callers.
    /// </summary>
    IAsyncEnumerable<CompletionEvent> CompleteAsync(
        IReadOnlyList<Message> messages,
        string model,
        CompletionOptions? options = null,
        CancellationToken ct = default);

    /// <summary>
    /// Returns the context window size in tokens for the given model, or null if the
    /// provider cannot determine it (e.g. unknown model, misconfiguration). Callers fall
    /// back to <see cref="CompactionConfig.MaxContextTokens"/>.
    /// </summary>
    int? GetContextWindow(string model);
}
