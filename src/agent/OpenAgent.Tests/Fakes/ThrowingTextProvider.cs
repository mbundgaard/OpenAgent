using System.Runtime.CompilerServices;
using System.Text.Json;
using OpenAgent.Contracts;
using OpenAgent.Models.Common;
using OpenAgent.Models.Conversations;
using OpenAgent.Models.Providers;

namespace OpenAgent.Tests.Fakes;

/// <summary>
/// Text provider that always throws InvalidOperationException on CompleteAsync.
/// Used to test error handling in the message handler.
/// </summary>
public sealed class ThrowingTextProvider : ILlmTextProvider
{
    public string Key => "throwing-text";
    public IReadOnlyList<ProviderConfigField> ConfigFields => [];
    public void Configure(JsonElement configuration) { }

    public async IAsyncEnumerable<CompletionEvent> CompleteAsync(
        Conversation conversation,
        string userInput,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        throw new InvalidOperationException("LLM provider failed");
#pragma warning disable CS0162 // Unreachable code
        yield break;
#pragma warning restore CS0162
    }
}
