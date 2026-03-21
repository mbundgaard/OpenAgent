using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using OpenAgent.Contracts;
using OpenAgent.Models.Conversations;

namespace OpenAgent.Tests;

public class ConversationEndpointTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public ConversationEndpointTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task GetConversation_Exists_ReturnsIt()
    {
        var store = _factory.Services.GetRequiredService<IConversationStore>();
        var conversation = store.GetOrCreate(Guid.NewGuid().ToString(), "app", ConversationType.Text, "test-provider", "test-model");
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", "dev-api-key-change-me");

        var response = await client.GetAsync($"/api/conversations/{conversation.Id}");

        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<ConversationResponse>();
        Assert.Equal(conversation.Id, body!.Id);
    }

    [Fact]
    public async Task GetConversation_NotFound_Returns404()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", "dev-api-key-change-me");

        var response = await client.GetAsync("/api/conversations/does-not-exist");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task DeleteConversation_ReturnsNoContent()
    {
        var store = _factory.Services.GetRequiredService<IConversationStore>();
        var conversation = store.GetOrCreate(Guid.NewGuid().ToString(), "app", ConversationType.Text, "test-provider", "test-model");
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", "dev-api-key-change-me");

        var response = await client.DeleteAsync($"/api/conversations/{conversation.Id}");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        var getResponse = await client.GetAsync($"/api/conversations/{conversation.Id}");
        Assert.Equal(HttpStatusCode.NotFound, getResponse.StatusCode);
    }

    private record ConversationResponse(string Id);
}
