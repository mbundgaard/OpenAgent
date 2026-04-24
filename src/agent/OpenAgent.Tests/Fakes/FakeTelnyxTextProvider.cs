using System.Runtime.CompilerServices;
using System.Text.Json;
using OpenAgent.Contracts;
using OpenAgent.Models.Common;
using OpenAgent.Models.Conversations;
using OpenAgent.Models.Providers;

namespace OpenAgent.Tests.Fakes;

/// <summary>
/// Returns a single canned reply as TextDelta events. Records the last
/// conversation and user message for assertions.
/// </summary>
public sealed class FakeTelnyxTextProvider : ILlmTextProvider
{
    private readonly string _reply;

    public Conversation? LastConversation { get; private set; }
    public Message? LastUserMessage { get; private set; }

    public FakeTelnyxTextProvider(string reply) { _reply = reply; }

    // IConfigurable — no-op for tests
    public string Key => "fake-telnyx-text";
    public IReadOnlyList<ProviderConfigField> ConfigFields => [];
    public void Configure(JsonElement configuration) { }
    public int? GetContextWindow(string model) => null;

    public async IAsyncEnumerable<CompletionEvent> CompleteAsync(
        Conversation conversation,
        Message userMessage,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        LastConversation = conversation;
        LastUserMessage = userMessage;
        await Task.Yield();
        yield return new TextDelta(_reply);
    }

    public async IAsyncEnumerable<CompletionEvent> CompleteAsync(
        IReadOnlyList<Message> messages,
        string model,
        CompletionOptions? options = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.Yield();
        yield return new TextDelta(_reply);
    }
}
