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
}
