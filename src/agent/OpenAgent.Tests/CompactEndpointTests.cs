using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OpenAgent.Contracts;
using OpenAgent.Models.Conversations;

namespace OpenAgent.Tests;

/// <summary>
/// Tests POST /api/conversations/{id}/compact — the manual compaction trigger.
/// </summary>
public class CompactEndpointTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly FakeCompactionSummarizer _fakeSummarizer = new("## Summary\nManual compaction summary.");

    public CompactEndpointTests(WebApplicationFactory<Program> factory)
    {
        TestSetup.EnsureConfigSeeded();
        _factory = factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                services.RemoveAll(typeof(ICompactionSummarizer));
                services.AddSingleton<ICompactionSummarizer>(_fakeSummarizer);

                // Override CompactionConfig with tiny budgets so the test can trigger a cut
                // without seeding thousands of messages.
                services.RemoveAll(typeof(CompactionConfig));
                services.AddSingleton(new CompactionConfig { KeepRecentTokens = 50 });
            });
        });
    }

    private HttpClient CreateAuthenticatedClient()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", "dev-api-key-change-me");
        return client;
    }

    [Fact]
    public async Task Compact_returns_not_found_for_unknown_conversation()
    {
        var client = CreateAuthenticatedClient();
        var response = await client.PostAsJsonAsync("/api/conversations/does-not-exist/compact", new { });
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Compact_returns_ok_with_false_when_nothing_to_compact()
    {
        var store = _factory.Services.GetRequiredService<IConversationStore>();
        var conversationId = Guid.NewGuid().ToString();
        store.GetOrCreate(conversationId, "app", "azure-openai-text", "gpt-5.2-chat", "azure-openai-text", "gpt-5.2-chat");

        var client = CreateAuthenticatedClient();
        var response = await client.PostAsJsonAsync($"/api/conversations/{conversationId}/compact", new { });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(body.GetProperty("compacted").GetBoolean());
    }

    [Fact]
    public async Task Compact_with_instructions_forwards_to_summarizer()
    {
        var store = _factory.Services.GetRequiredService<IConversationStore>();
        var conversationId = Guid.NewGuid().ToString();
        store.GetOrCreate(conversationId, "app", "azure-openai-text", "gpt-5.2-chat", "azure-openai-text", "gpt-5.2-chat");

        // Seed enough messages to cross the KeepRecentTokens budget. IDs include the
        // conversationId so reruns don't collide on the global Messages.Id primary key.
        for (var i = 0; i < 20; i++)
        {
            store.AddMessage(conversationId, new Message
            {
                Id = $"{conversationId}-m{i}",
                ConversationId = conversationId,
                Role = i % 2 == 0 ? "user" : "assistant",
                Content = new string('x', 200)
            });
        }

        var client = CreateAuthenticatedClient();
        var response = await client.PostAsJsonAsync(
            $"/api/conversations/{conversationId}/compact",
            new { instructions = "focus on auth decisions" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("compacted").GetBoolean());
        Assert.Equal("focus on auth decisions", _fakeSummarizer.LastCustomInstructions);
    }

    private sealed class FakeCompactionSummarizer(string context) : ICompactionSummarizer
    {
        public string? LastCustomInstructions { get; private set; }

        public Task<CompactionResult> SummarizeAsync(
            string? existingContext,
            IReadOnlyList<Message> messages,
            string? customInstructions = null,
            CancellationToken ct = default)
        {
            LastCustomInstructions = customInstructions;
            return Task.FromResult(new CompactionResult { Context = context });
        }
    }
}
