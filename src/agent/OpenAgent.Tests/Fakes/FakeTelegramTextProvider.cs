using System.Runtime.CompilerServices;
using System.Text.Json;
using OpenAgent.Contracts;
using OpenAgent.Models.Common;
using OpenAgent.Models.Conversations;
using OpenAgent.Models.Providers;

namespace OpenAgent.Tests.Fakes;

/// <summary>
/// Fake text provider that yields a single TextDelta with the configured response.
/// </summary>
public sealed class FakeTelegramTextProvider : ILlmTextProvider
{
    private readonly string _response;

    public FakeTelegramTextProvider(string response)
    {
        _response = response;
    }

    public string Key => "fake-telegram-text";
    public IReadOnlyList<ProviderConfigField> ConfigFields => [];
    public void Configure(JsonElement configuration) { }

    public async IAsyncEnumerable<CompletionEvent> CompleteAsync(
        Conversation conversation,
        string userInput,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        yield return new TextDelta(_response);
        await Task.CompletedTask;
    }
}
