using System.Runtime.CompilerServices;
using System.Text.Json;
using OpenAgent.Contracts;
using OpenAgent.Models.Common;
using OpenAgent.Models.Conversations;
using OpenAgent.Models.Providers;

namespace OpenAgent.Tests.Fakes;

/// <summary>
/// Text provider that reproduces a realistic multi-round tool-call turn, mirroring exactly what
/// the real providers (AnthropicSubscriptionTextProvider, AzureOpenAiTextProvider) do to
/// conversation history and events: one or more rounds of
/// TextDelta* -&gt; ToolCallEvent -&gt; ToolResultEvent, followed by a final round whose text is
/// either persisted as the assistant message or discarded via the "[]" sentinel.
///
/// Text emitted before a tool call is scratch - the real providers never persist it as assistant
/// content (only the ToolCalls field is set on that round's stored message), and TextDelta is
/// re-yielded per round from an accumulator declared INSIDE the round loop. PersistingTextProvider
/// (single-round only) cannot exercise this - it never emits a ToolCallEvent, so a consumer bug
/// that concatenates TextDelta across rounds instead of resetting on each tool call goes
/// undetected. This fake exists specifically to catch that bug class.
/// </summary>
public sealed class MultiRoundTextProvider : ILlmTextProvider
{
    private readonly IConversationStore _store;
    private readonly IReadOnlyList<string> _toolRoundNarrations;
    private readonly string _finalText;

    /// <param name="store">Store to persist against, mirroring what the real providers do.</param>
    /// <param name="toolRoundNarrations">Text emitted immediately before each simulated tool-call
    /// round. The real providers never persist this as assistant content - it precedes a tool
    /// call, so only the ToolCalls field is set on that round's stored message.</param>
    /// <param name="finalText">Text of the final round. Pass <see cref="ResponseSuppression.Sentinel"/>
    /// to simulate a suppressed turn (the whole turn is deleted from history).</param>
    public MultiRoundTextProvider(IConversationStore store, IReadOnlyList<string> toolRoundNarrations, string finalText)
    {
        _store = store;
        _toolRoundNarrations = toolRoundNarrations;
        _finalText = finalText;
    }

    /// <summary>Content of every user message this provider was asked to complete.</summary>
    public List<string> PersistedUserContents { get; } = [];

    /// <summary>
    /// Content of the final assistant message actually persisted to the store, or null if the
    /// turn was suppressed. Tests compare delivered text against this to prove the two never
    /// diverge.
    /// </summary>
    public string? PersistedAssistantContent { get; private set; }

    public string Key => "multi-round-text";
    public IReadOnlyList<ProviderConfigField> ConfigFields => [];
    public void Configure(JsonElement configuration) { }
    public int? GetContextWindow(string model) => null;

    public async IAsyncEnumerable<CompletionEvent> CompleteAsync(
        Conversation conversation,
        Message userMessage,
        [EnumeratorCancellation] CancellationToken ct = default,
        bool persistUserMessage = true,
        string? modelOverride = null)
    {
        PersistedUserContents.Add(userMessage.Content ?? "");
        var turnMessageIds = new List<string>();
        if (persistUserMessage)
        {
            _store.AddMessage(conversation.Id, userMessage);
            turnMessageIds.Add(userMessage.Id);
        }

        var toolCallIndex = 0;
        foreach (var narration in _toolRoundNarrations)
        {
            if (narration.Length > 0)
                yield return new TextDelta(narration);
            await Task.Yield();

            var toolCallId = $"call-{++toolCallIndex}";

            // Mirror the real providers: text preceding a tool call is never persisted as
            // assistant content - only ToolCalls is set on this round's stored message.
            var toolCallMessageId = Guid.NewGuid().ToString();
            turnMessageIds.Add(toolCallMessageId);
            _store.AddMessage(conversation.Id, new Message
            {
                Id = toolCallMessageId,
                ConversationId = conversation.Id,
                Role = "assistant",
                ToolCalls = $"[{{\"id\":\"{toolCallId}\",\"function\":{{\"name\":\"file_append\"}}}}]",
                Modality = MessageModality.Text
            });

            yield return new ToolCallEvent(toolCallId, "file_append", "{}");

            var toolResultMessageId = Guid.NewGuid().ToString();
            turnMessageIds.Add(toolResultMessageId);
            _store.AddMessage(conversation.Id, new Message
            {
                Id = toolResultMessageId,
                ConversationId = conversation.Id,
                Role = "tool",
                Content = "ok",
                ToolCallId = toolCallId,
                Modality = MessageModality.Text
            });

            yield return new ToolResultEvent(toolCallId, "file_append", "ok");
        }

        // Final round.
        if (_finalText.Length > 0)
            yield return new TextDelta(_finalText);
        await Task.Yield();

        if (ResponseSuppression.IsSuppressed(_finalText))
        {
            // Mirror the real providers: the sentinel discards the ENTIRE turn, tool rounds
            // included.
            _store.DeleteMessages(conversation.Id, turnMessageIds);
            yield return new ResponseSuppressed();
            yield break;
        }

        var assistantMessageId = Guid.NewGuid().ToString();
        _store.AddMessage(conversation.Id, new Message
        {
            Id = assistantMessageId,
            ConversationId = conversation.Id,
            Role = "assistant",
            Content = _finalText,
            Modality = MessageModality.Text
        });
        PersistedAssistantContent = _finalText;

        yield return new AssistantMessageSaved(assistantMessageId);
    }

    public async IAsyncEnumerable<CompletionEvent> CompleteAsync(
        IReadOnlyList<Message> messages,
        string model,
        CompletionOptions? options = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        yield return new TextDelta(_finalText);
        await Task.Yield();
    }
}
