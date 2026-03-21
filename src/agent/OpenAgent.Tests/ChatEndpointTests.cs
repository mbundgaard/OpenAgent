using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OpenAgent.Contracts;
using OpenAgent.Models.Common;
using OpenAgent.Models.Configs;
using OpenAgent.Models.Conversations;
using OpenAgent.Models.Providers;

namespace OpenAgent.Tests;

public class ChatEndpointTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public ChatEndpointTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                services.RemoveAll(typeof(ILlmTextProvider));
                // Register fake as keyed service — endpoint resolves by conversation.Provider key
                var fake = new FakeTextProvider();
                services.AddKeyedSingleton<ILlmTextProvider>("azure-openai-text", fake);
                services.AddSingleton<ILlmTextProvider>(fake);
            });
        });
    }

    [Fact]
    public async Task SendMessage_NewConversation_ReturnsCompletionEvents()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", "dev-api-key-change-me");
        var conversationId = Guid.NewGuid().ToString();

        var response = await client.PostAsJsonAsync(
            $"/api/conversations/{conversationId}/messages",
            new { Content = "hello" });

        response.EnsureSuccessStatusCode();

        var events = await response.Content.ReadFromJsonAsync<JsonElement[]>();
        Assert.NotNull(events);
        Assert.Equal(2, events.Length);
        Assert.Equal("text", events[0].GetProperty("type").GetString());
        Assert.Equal("fake ", events[0].GetProperty("content").GetString());
        Assert.Equal("text", events[1].GetProperty("type").GetString());
        Assert.Equal("response", events[1].GetProperty("content").GetString());
    }

    [Fact]
    public async Task SendMessage_ExistingConversation_ReturnsCompletionEvents()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", "dev-api-key-change-me");
        var conversationId = Guid.NewGuid().ToString();

        // First message creates the conversation
        await client.PostAsJsonAsync(
            $"/api/conversations/{conversationId}/messages",
            new { Content = "hello" });

        // Second message reuses it
        var response = await client.PostAsJsonAsync(
            $"/api/conversations/{conversationId}/messages",
            new { Content = "follow up" });

        response.EnsureSuccessStatusCode();

        var events = await response.Content.ReadFromJsonAsync<JsonElement[]>();
        Assert.NotNull(events);
        Assert.True(events.Length > 0);
        Assert.Equal("text", events[0].GetProperty("type").GetString());
    }

    private sealed class FakeTextProvider : ILlmTextProvider
    {
        public string Key => "text-provider";
        public IReadOnlyList<ProviderConfigField> ConfigFields => [];
        public void Configure(JsonElement configuration) { }

        public async IAsyncEnumerable<CompletionEvent> CompleteAsync(Conversation conversation, Message userMessage,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            yield return new TextDelta("fake ");
            yield return new TextDelta("response");
            await Task.CompletedTask;
        }

        public async IAsyncEnumerable<CompletionEvent> CompleteAsync(IReadOnlyList<Message> messages, string model,
            CompletionOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            yield return new TextDelta("fake raw response");
            await Task.CompletedTask;
        }
    }
}
