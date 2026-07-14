using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using OpenAgent.Contracts;
using OpenAgent.Models.Common;
using OpenAgent.Models.Configs;
using OpenAgent.Models.Conversations;
using OpenAgent.Models.Providers;
using OpenAgent.ScheduledTasks;
using OpenAgent.ScheduledTasks.Models;
using OpenAgent.Tests.Fakes;

namespace OpenAgent.Tests;

/// <summary>
/// Covers how ScheduledTaskExecutor surfaces the "[]" suppression sentinel to its caller.
/// The provider does the actual turn-discard; the executor only needs to flag it so the
/// service skips delivery for a periodic check that found nothing.
/// </summary>
public class ScheduledTaskExecutorTests
{
    private static ScheduledTask MakeTask() => new()
    {
        Id = Guid.NewGuid().ToString(),
        Name = "wishlist recheck",
        Prompt = "Check the wishlist for new hits",
        Schedule = new ScheduleConfig { IntervalMs = 60_000 }
    };

    private static ScheduledTaskExecutor MakeExecutor(IConversationStore store, ILlmTextProvider provider)
    {
        var config = new AgentConfig
        {
            TextProvider = "fake",
            TextModel = "fake-model",
            VoiceProvider = "fake",
            VoiceModel = "fake-voice"
        };
        return new ScheduledTaskExecutor(store, _ => provider, config, NullLogger<ScheduledTaskExecutor>.Instance);
    }

    [Fact]
    public async Task ExecuteAsync_normal_response_is_not_suppressed()
    {
        var store = new InMemoryConversationStore();
        var executor = MakeExecutor(store, new FakeProvider([new TextDelta("all done")]));

        var result = await executor.ExecuteAsync(MakeTask(), promptOverride: null, CancellationToken.None);

        Assert.False(result.Suppressed);
        Assert.Equal("all done", result.Response);
    }

    [Fact]
    public async Task ExecuteAsync_response_suppressed_event_sets_suppressed_flag()
    {
        var store = new InMemoryConversationStore();
        // Mirrors what the real providers emit on the "[]" sentinel: the streamed text,
        // then ResponseSuppressed once the turn has been discarded from history.
        var executor = MakeExecutor(store, new FakeProvider([new TextDelta("[]"), new ResponseSuppressed()]));

        var result = await executor.ExecuteAsync(MakeTask(), promptOverride: null, CancellationToken.None);

        Assert.True(result.Suppressed);
    }

    /// <summary>Emits a fixed event sequence — stands in for a real text provider.</summary>
    private sealed class FakeProvider(IReadOnlyList<CompletionEvent> events) : ILlmTextProvider
    {
        public string Key => "fake";
        public IReadOnlyList<ProviderConfigField> ConfigFields => [];
        public void Configure(JsonElement configuration) { }
        public int? GetContextWindow(string model) => null;

        public async IAsyncEnumerable<CompletionEvent> CompleteAsync(Conversation conversation, Message userMessage,
            [EnumeratorCancellation] CancellationToken ct = default,
            bool persistUserMessage = true)
        {
            foreach (var evt in events)
                yield return evt;
            await Task.CompletedTask;
        }

        public async IAsyncEnumerable<CompletionEvent> CompleteAsync(IReadOnlyList<Message> messages, string model,
            CompletionOptions? options = null,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            yield return new TextDelta(string.Empty);
            await Task.CompletedTask;
        }
    }
}
