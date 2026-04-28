using System.Text.Json;
using OpenAgent.Contracts;
using OpenAgent.Models.Common;
using OpenAgent.Models.Conversations;
using OpenAgent.Models.Providers;
using OpenAgent.Models.Voice;

namespace OpenAgent.Tests.ConversationTools;

/// <summary>
/// Minimal ILlmTextProvider fake that exposes a key and model list for conversation tool tests.
/// </summary>
internal sealed class FakeModelProvider(string key, string[] models) : ILlmTextProvider
{
    public string Key => key;
    public IReadOnlyList<ProviderConfigField> ConfigFields => [];
    public IReadOnlyList<string> Models => models;
    public void Configure(JsonElement configuration) { }
    public int? GetContextWindow(string model) => null;

    public IAsyncEnumerable<CompletionEvent> CompleteAsync(
        Models.Conversations.Conversation conversation, Message userMessage, CancellationToken ct = default)
        => throw new NotImplementedException();

    public IAsyncEnumerable<CompletionEvent> CompleteAsync(
        IReadOnlyList<Message> messages, string model,
        CompletionOptions? options = null, CancellationToken ct = default)
        => throw new NotImplementedException();
}

/// <summary>
/// Minimal ILlmVoiceProvider fake mirroring FakeModelProvider for the voice modality.
/// </summary>
internal sealed class FakeVoiceModelProvider(string key, string[] models) : ILlmVoiceProvider
{
    public string Key => key;
    public IReadOnlyList<ProviderConfigField> ConfigFields => [];
    public IReadOnlyList<string> Models => models;
    public void Configure(JsonElement configuration) { }

    public Task<IVoiceSession> StartSessionAsync(
        Models.Conversations.Conversation conversation,
        VoiceSessionOptions? options = null,
        CancellationToken ct = default)
        => throw new NotImplementedException();
}
