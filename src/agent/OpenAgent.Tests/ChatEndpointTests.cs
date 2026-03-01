using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using OpenAgent.Contracts;
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
                // Replace real provider with a fake
                var descriptor = services.SingleOrDefault(d => d.ServiceType == typeof(ILlmTextProvider));
                if (descriptor is not null) services.Remove(descriptor);
                services.AddSingleton<ILlmTextProvider, FakeTextProvider>();
            });
        });
    }

    [Fact]
    public async Task SendMessage_ConversationNotFound_Returns404()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/api/conversations/does-not-exist/messages",
            new { Content = "hello" });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task SendMessage_ValidConversation_ReturnsAssistantResponse()
    {
        var client = _factory.CreateClient();

        // Create a conversation first
        var createResponse = await client.PostAsync("/api/conversations", null);
        var created = await createResponse.Content.ReadFromJsonAsync<ConversationResponse>();

        // Send a message
        var response = await client.PostAsJsonAsync(
            $"/api/conversations/{created!.Id}/messages",
            new { Content = "hello" });

        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<ChatResponse>();
        Assert.Equal("assistant", body!.Role);
        Assert.Equal("fake response", body.Content);
    }

    private record ConversationResponse(string Id);
    private record ChatResponse(string Role, string Content);

    private sealed class FakeTextProvider : ILlmTextProvider
    {
        public IReadOnlyList<ProviderConfigField> ConfigFields => [];
        public void Configure(JsonElement configuration) { }

        public Task<TextResponse> CompleteAsync(string conversationId, string userInput, CancellationToken ct = default)
        {
            return Task.FromResult(new TextResponse { Role = "assistant", Content = "fake response" });
        }
    }
}
