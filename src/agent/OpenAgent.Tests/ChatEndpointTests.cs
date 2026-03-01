using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using OpenAgent.Contracts;
using OpenAgent.Models.Conversations;
using OpenAgent.Models.Providers;
using OpenAgent.Models.Text;

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
                var descriptor = services.SingleOrDefault(d => d.ServiceType == typeof(ILlmTextProvider));
                if (descriptor is not null) services.Remove(descriptor);
                services.AddSingleton<ILlmTextProvider, FakeTextProvider>();
            });
        });
    }

    [Fact]
    public async Task SendMessage_NewConversation_CreatesAndReturnsResponse()
    {
        var client = _factory.CreateClient();
        var conversationId = Guid.NewGuid().ToString();

        var response = await client.PostAsJsonAsync(
            $"/api/conversations/{conversationId}/messages",
            new { Content = "hello" });

        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<ChatResponse>();
        Assert.Equal(conversationId, body!.ConversationId);
        Assert.Equal("assistant", body.Role);
        Assert.Equal("fake response", body.Content);
    }

    [Fact]
    public async Task SendMessage_ExistingConversation_ReturnsResponse()
    {
        var client = _factory.CreateClient();
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
        var body = await response.Content.ReadFromJsonAsync<ChatResponse>();
        Assert.Equal(conversationId, body!.ConversationId);
        Assert.Equal("assistant", body.Role);
    }

    private record ChatResponse(string ConversationId, string Role, string Content);

    private sealed class FakeTextProvider : ILlmTextProvider
    {
        public IReadOnlyList<ProviderConfigField> ConfigFields => [];
        public void Configure(JsonElement configuration) { }

        public Task<TextResponse> CompleteAsync(Conversation conversation, string userInput, CancellationToken ct = default)
        {
            return Task.FromResult(new TextResponse { Role = "assistant", Content = "fake response" });
        }
    }
}
