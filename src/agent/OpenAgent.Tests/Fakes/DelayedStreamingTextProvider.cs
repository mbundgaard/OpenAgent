using System.Runtime.CompilerServices;
using System.Text.Json;
using OpenAgent.Contracts;
using OpenAgent.Models.Common;
using OpenAgent.Models.Conversations;
using OpenAgent.Models.Providers;

namespace OpenAgent.Tests.Fakes;

/// <summary>
/// Fake text provider that yields TextDelta events with a fixed delay between each,
/// keeping the producer alive across multiple draft-consumer ticks so streaming
/// behavior (draft cadence, mid-stream degradation) can be observed deterministically.
/// </summary>
public sealed class DelayedStreamingTextProvider : ILlmTextProvider
{
    private readonly TimeSpan _delay;
    private readonly string[] _tokens;

    public DelayedStreamingTextProvider(TimeSpan delay, params string[] tokens)
    {
        _delay = delay;
        _tokens = tokens;
    }

    public string Key => "delayed-streaming-text";
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
            await Task.Delay(_delay, ct);
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
            await Task.Delay(_delay, ct);
        }
    }
}
