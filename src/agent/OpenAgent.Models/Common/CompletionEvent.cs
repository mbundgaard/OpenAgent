namespace OpenAgent.Models.Common;

/// <summary>
/// An event produced during an LLM completion. Covers text output,
/// tool invocations, and tool results.
/// </summary>
public abstract record CompletionEvent;

/// <summary>
/// A chunk of text content from the LLM.
/// </summary>
public sealed record TextDelta(string Content) : CompletionEvent;

/// <summary>
/// The LLM decided to call a tool. Emitted once arguments are fully accumulated.
/// </summary>
public sealed record ToolCallEvent(string ToolCallId, string Name, string Arguments) : CompletionEvent;

/// <summary>
/// A tool finished executing. Contains the tool's output.
/// </summary>
public sealed record ToolResultEvent(string ToolCallId, string Name, string Result) : CompletionEvent;

/// <summary>
/// The final assistant message has been persisted. Carries the internal message ID
/// so channel providers can associate it with their channel-specific message ID.
/// </summary>
public sealed record AssistantMessageSaved(string MessageId) : CompletionEvent;

/// <summary>
/// Emitted once per user turn when the LLM's response requires tool execution.
/// Signals that one or more tool-call rounds are about to begin.
/// </summary>
public sealed record ToolCallStarted : CompletionEvent;

/// <summary>
/// Emitted once per user turn after all tool-call rounds have completed and
/// the LLM is producing its final text response.
/// </summary>
public sealed record ToolCallCompleted : CompletionEvent;
