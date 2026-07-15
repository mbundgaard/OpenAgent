using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using OpenAgent.Contracts;
using OpenAgent.Models.Common;
using OpenAgent.Models.Conversations;
using OpenAgent.Models.Providers;

namespace OpenAgent.Tests.Fakes;

/// <summary>
/// Text provider that mimics what the real providers do to the store: it persists the incoming
/// user message, then persists an assistant reply. StreamingTextProvider writes nothing, which
/// would make "the nudge is not persisted" assertions pass vacuously.
///
/// When the configured reply is the "[]" sentinel it mimics suppression instead: the whole turn
/// (user message included) is deleted and ResponseSuppressed is emitted.
///
/// Honours persistUserMessage like the real providers do: when false, the user message is never
/// written to the store at all (not persisted-then-deleted) — it is recorded mid-turn (before
/// any yield) in <see cref="StoreContentsDuringTurn"/> so tests can prove the store never
/// contained it, not merely that it was cleaned up afterwards.
/// </summary>
public sealed class PersistingTextProvider : ILlmTextProvider
{
    private readonly IConversationStore _store;
    private readonly string _reply;

    public PersistingTextProvider(IConversationStore store, string reply)
    {
        _store = store;
        _reply = reply;
    }

    /// <summary>Content of every user message this provider was asked to complete.</summary>
    public List<string> PersistedUserContents { get; } = [];

    /// <summary>The modelOverride argument passed on the most recent CompleteAsync call.</summary>
    public string? LastModelOverride { get; private set; }

    /// <summary>
    /// Snapshot of every message's Content currently in the store, taken immediately after the
    /// (possibly skipped) persistence step and before the reply is yielded — i.e. exactly the
    /// state a concurrent turn on the same conversation would observe while this one is in
    /// flight. Proves the nudge is absent mid-turn, not just after cleanup.
    /// </summary>
    public List<string?> StoreContentsDuringTurn { get; } = [];

    public string Key => "persisting-text";
    public IReadOnlyList<ProviderConfigField> ConfigFields => [];
    public void Configure(JsonElement configuration) { }
    public int? GetContextWindow(string model) => null;

    public async IAsyncEnumerable<CompletionEvent> CompleteAsync(
        Conversation conversation,
        Message userMessage,
        [EnumeratorCancellation] CancellationToken ct = default,
        bool persistUserMessage = true,
        string? modelOverride = null,
        string? thinkingOverride = null)
    {
        LastModelOverride = modelOverride;
        PersistedUserContents.Add(userMessage.Content ?? "");
        if (persistUserMessage)
            _store.AddMessage(conversation.Id, userMessage);

        // Mid-turn snapshot — this is what a concurrent turn reading the same conversation would
        // see right now, before this turn has yielded anything back to its caller.
        StoreContentsDuringTurn.AddRange(_store.GetMessages(conversation.Id).Select(m => m.Content));

        yield return new TextDelta(_reply);
        await Task.Yield();

        if (ResponseSuppression.IsSuppressed(_reply))
        {
            // Mirror the real providers: the sentinel discards the entire turn. Only delete the
            // user message if it was actually persisted — an ephemeral nudge was never written,
            // so there is nothing to delete for it.
            if (persistUserMessage)
                _store.DeleteMessages(conversation.Id, [userMessage.Id]);
            yield return new ResponseSuppressed();
            yield break;
        }

        _store.AddMessage(conversation.Id, new Message
        {
            Id = Guid.NewGuid().ToString(),
            ConversationId = conversation.Id,
            Role = "assistant",
            Content = _reply,
            Modality = MessageModality.Text
        });
    }

    public async IAsyncEnumerable<CompletionEvent> CompleteAsync(
        IReadOnlyList<Message> messages,
        string model,
        CompletionOptions? options = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        yield return new TextDelta(_reply);
        await Task.Yield();
    }
}
