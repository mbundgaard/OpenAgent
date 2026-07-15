using System.Runtime.CompilerServices;
using System.Text.Json;
using OpenAgent.Contracts;
using OpenAgent.Models.Common;
using OpenAgent.Models.Conversations;
using OpenAgent.Models.Providers;

namespace OpenAgent.Tests.Fakes;

/// <summary>
/// Fake text provider for streaming-degradation tests. Emits the pre-gate tokens, then blocks
/// until <paramref name="gate"/> completes (typically signalled by the sender when the first
/// rich draft is attempted), then emits the post-gate tokens with a delay between each. Gating
/// on the attempt — rather than a fixed sleep — removes the "producer finished before the first
/// draft tick" race; the post-gate tokens keep the stream alive for the follow-up ticks that
/// exercise the degraded (plain) path.
/// </summary>
public sealed class GatedStreamingTextProvider : ILlmTextProvider
{
    private readonly Task _gate;
    private readonly string[] _preGateTokens;
    private readonly string[] _postGateTokens;
    private readonly TimeSpan _postGateDelay;

    public GatedStreamingTextProvider(Task gate, string[] preGateTokens, string[] postGateTokens, TimeSpan postGateDelay)
    {
        _gate = gate;
        _preGateTokens = preGateTokens;
        _postGateTokens = postGateTokens;
        _postGateDelay = postGateDelay;
    }

    public string Key => "gated-streaming-text";
    public IReadOnlyList<ProviderConfigField> ConfigFields => [];
    public void Configure(JsonElement configuration) { }
    public int? GetContextWindow(string model) => null;

    private async IAsyncEnumerable<CompletionEvent> ProduceAsync([EnumeratorCancellation] CancellationToken ct)
    {
        foreach (var token in _preGateTokens)
        {
            yield return new TextDelta(token);
            await Task.Yield();
        }

        // Block until the consumer has attempted the first rich draft (bounded so a broken
        // implementation surfaces as a timeout rather than a hung test run).
        await _gate.WaitAsync(TimeSpan.FromSeconds(5), ct);

        foreach (var token in _postGateTokens)
        {
            yield return new TextDelta(token);
            await Task.Delay(_postGateDelay, ct);
        }
    }

    public IAsyncEnumerable<CompletionEvent> CompleteAsync(
        Conversation conversation,
        Message userMessage,
        CancellationToken ct = default,
        bool persistUserMessage = true,
        string? modelOverride = null) => ProduceAsync(ct);

    public IAsyncEnumerable<CompletionEvent> CompleteAsync(
        IReadOnlyList<Message> messages,
        string model,
        CompletionOptions? options = null,
        CancellationToken ct = default) => ProduceAsync(ct);
}
