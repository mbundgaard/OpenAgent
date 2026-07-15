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
    /// <param name="conversation">The conversation the turn runs in.</param>
    /// <param name="userMessage">
    /// The user message driving this turn. When <paramref name="persistUserMessage"/> is
    /// <see langword="true"/> (the default) it is written to the conversation store before the
    /// turn begins, exactly like every other message. Callers pass <see langword="false"/> for
    /// messages that must stay ephemeral — visible to the LLM for this turn only, never written
    /// to the store. This exists for the background-agent heartbeat: the nudge that wakes the
    /// agent up must not be readable by a concurrent turn on the same conversation (e.g. a real
    /// user message arriving on Telegram while the heartbeat is in flight) and must never
    /// require cleanup if the process dies mid-turn.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <param name="persistUserMessage">See remarks above.</param>
    /// <param name="modelOverride">
    /// When non-empty, the wire request uses this model instead of <c>conversation.TextModel</c>.
    /// Everything else driving the turn - history, conversation id, context window, active skills -
    /// still comes from <paramref name="conversation"/>. Lets a caller (e.g. the background-agent
    /// heartbeat) run a turn on a cheaper model than the conversation's own, without mutating the
    /// conversation's persisted model. Null or empty means inherit <c>conversation.TextModel</c>,
    /// matching every existing caller's behavior.
    /// </param>
    /// <param name="thinkingOverride">
    /// When set, overrides the configured thinking/effort spec ("off" or low/medium/high/xhigh/max)
    /// for this turn only — used by the background-agent heartbeat to run with thinking off
    /// regardless of the main-chat setting. Null means use <c>AgentConfig.TextThinking</c>.
    /// Providers that don't support Anthropic-style thinking ignore this parameter.
    /// </param>
    IAsyncEnumerable<CompletionEvent> CompleteAsync(
        Conversation conversation, Message userMessage, CancellationToken ct = default, bool persistUserMessage = true, string? modelOverride = null, string? thinkingOverride = null);

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
