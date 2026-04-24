using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
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
        TestSetup.EnsureConfigSeeded();
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

    [Fact]
    public async Task ListConversations_OrdersByLastActivity()
    {
        var store = _factory.Services.GetRequiredService<IConversationStore>();

        // Create 3 conversations with distinct unique IDs we can identify
        var idA = $"order-test-A-{Guid.NewGuid()}";
        var idB = $"order-test-B-{Guid.NewGuid()}";
        var idC = $"order-test-C-{Guid.NewGuid()}";

        var convA = store.GetOrCreate(idA, "app", ConversationType.Text, "test-provider", "test-model");
        var convB = store.GetOrCreate(idB, "app", ConversationType.Text, "test-provider", "test-model");
        var convC = store.GetOrCreate(idC, "app", ConversationType.Text, "test-provider", "test-model");

        // Set distinct LastActivity values: B newest, A middle, C oldest
        convA.LastActivity = DateTimeOffset.UtcNow.AddMinutes(-5);
        convB.LastActivity = DateTimeOffset.UtcNow;
        convC.LastActivity = DateTimeOffset.UtcNow.AddMinutes(-10);
        store.Update(convA);
        store.Update(convB);
        store.Update(convC);

        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", "dev-api-key-change-me");

        var response = await client.GetAsync("/api/conversations");
        response.EnsureSuccessStatusCode();
        var list = await response.Content.ReadFromJsonAsync<List<ConversationResponse>>();
        Assert.NotNull(list);

        // Filter to just our 3 conversations and assert their relative order
        var ours = list.Where(c => c.Id == idA || c.Id == idB || c.Id == idC).Select(c => c.Id).ToList();
        Assert.Equal(new[] { idB, idA, idC }, ours);
    }

    [Fact]
    public async Task PatchConversation_MentionNames_RoundTrips()
    {
        var store = _factory.Services.GetRequiredService<IConversationStore>();
        var conversation = store.GetOrCreate(Guid.NewGuid().ToString(), "app", ConversationType.Text, "test-provider", "test-model");

        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", "dev-api-key-change-me");

        // Set a list
        var setResponse = await client.PatchAsJsonAsync(
            $"/api/conversations/{conversation.Id}",
            new { mention_names = new[] { "Dex", "fox" } });
        setResponse.EnsureSuccessStatusCode();

        var afterSet = await client.GetFromJsonAsync<JsonElement>($"/api/conversations/{conversation.Id}");
        var names = afterSet.GetProperty("mention_names").EnumerateArray().Select(e => e.GetString()).ToArray();
        Assert.Equal(new[] { "Dex", "fox" }, names);

        // Clear with empty list -> absent in response
        var clearResponse = await client.PatchAsJsonAsync(
            $"/api/conversations/{conversation.Id}",
            new { mention_names = Array.Empty<string>() });
        clearResponse.EnsureSuccessStatusCode();

        var afterClear = await client.GetFromJsonAsync<JsonElement>($"/api/conversations/{conversation.Id}");
        Assert.False(afterClear.TryGetProperty("mention_names", out _),
            "mention_names should be omitted when null (JsonIgnoreWhenWritingNull)");
    }

    private record ConversationResponse(string Id);
}
