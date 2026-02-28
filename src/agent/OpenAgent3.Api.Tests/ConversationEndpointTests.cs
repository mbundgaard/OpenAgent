using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace OpenAgent3.Api.Tests;

public class ConversationEndpointTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient _client;

    public ConversationEndpointTests(WebApplicationFactory<Program> factory)
    {
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task CreateConversation_ReturnsId()
    {
        var response = await _client.PostAsync("/api/conversations", null);

        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<ConversationResponse>();
        Assert.NotNull(body);
        Assert.False(string.IsNullOrEmpty(body.Id));
    }

    [Fact]
    public async Task GetConversation_AfterCreate_ReturnsIt()
    {
        var createResponse = await _client.PostAsync("/api/conversations", null);
        var created = await createResponse.Content.ReadFromJsonAsync<ConversationResponse>();

        var response = await _client.GetAsync($"/api/conversations/{created!.Id}");

        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<ConversationResponse>();
        Assert.Equal(created.Id, body!.Id);
    }

    [Fact]
    public async Task GetConversation_NotFound_Returns404()
    {
        var response = await _client.GetAsync("/api/conversations/does-not-exist");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task DeleteConversation_ReturnsNoContent()
    {
        var createResponse = await _client.PostAsync("/api/conversations", null);
        var created = await createResponse.Content.ReadFromJsonAsync<ConversationResponse>();

        var response = await _client.DeleteAsync($"/api/conversations/{created!.Id}");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        var getResponse = await _client.GetAsync($"/api/conversations/{created.Id}");
        Assert.Equal(HttpStatusCode.NotFound, getResponse.StatusCode);
    }

    private record ConversationResponse(string Id);
}
