using System.Runtime.CompilerServices;
using System.Text.Json;
using OpenAgent.Contracts;
using OpenAgent.Models.Common;
using OpenAgent.Models.Conversations;
using OpenAgent.Models.Providers;

namespace OpenAgent.Tests.Fakes;

/// <summary>
/// Fake text provider that yields multiple TextDelta events to simulate streaming.
/// </summary>
public sealed class StreamingTextProvider : ILlmTextProvider
{
    private readonly string[] _tokens;

    public StreamingTextProvider(params string[] tokens)
    {
        _tokens = tokens;
    }

    public string Key => "streaming-text";
    public IReadOnlyList<ProviderConfigField> ConfigFields => [];
    public void Configure(JsonElement configuration) { }
    public int? GetContextWindow(string model) => null;

    public async IAsyncEnumerable<CompletionEvent> CompleteAsync(
        Conversation conversation,
        Message userMessage,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        foreach (var token in _tokens)
        {
            yield return new TextDelta(token);
            await Task.Yield();
        }
    }

    public async IAsyncEnumerable<CompletionEvent> CompleteAsync(
        IReadOnlyList<Message> messages,
        string model,
        CompletionOptions? options = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        foreach (var token in _tokens)
        {
            yield return new TextDelta(token);
            await Task.Yield();
        }
    }
}
