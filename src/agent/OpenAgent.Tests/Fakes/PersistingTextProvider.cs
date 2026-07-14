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

    public string Key => "persisting-text";
    public IReadOnlyList<ProviderConfigField> ConfigFields => [];
    public void Configure(JsonElement configuration) { }
    public int? GetContextWindow(string model) => null;

    public async IAsyncEnumerable<CompletionEvent> CompleteAsync(
        Conversation conversation,
        Message userMessage,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        PersistedUserContents.Add(userMessage.Content ?? "");
        _store.AddMessage(conversation.Id, userMessage);

        yield return new TextDelta(_reply);
        await Task.Yield();

        if (ResponseSuppression.IsSuppressed(_reply))
        {
            // Mirror the real providers: the sentinel discards the entire turn.
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
