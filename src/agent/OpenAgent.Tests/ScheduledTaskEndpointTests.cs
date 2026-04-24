using System.Net;
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
using OpenAgent.ScheduledTasks.Models;

namespace OpenAgent.Tests;

public class ScheduledTaskEndpointTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public ScheduledTaskEndpointTests(WebApplicationFactory<Program> factory)
    {
        TestSetup.EnsureConfigSeeded();
        _factory = factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                services.RemoveAll(typeof(ILlmTextProvider));
                var fake = new FakeTextProvider();
                services.AddKeyedSingleton<ILlmTextProvider>("azure-openai-text", fake);
                services.AddSingleton<ILlmTextProvider>(fake);
            });
        });
    }

    private HttpClient CreateAuthenticatedClient()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", "dev-api-key-change-me");
        return client;
    }

    private static object MakeCronTask(string? taskId = null) => new
    {
        id = taskId ?? Guid.NewGuid().ToString(),
        name = "Test cron task",
        prompt = "Say hello",
        schedule = new { cron = "0 9 * * *" }
    };

    private static object MakeOneShotTask(string? taskId = null) => new
    {
        id = taskId ?? Guid.NewGuid().ToString(),
        name = "Test one-shot task",
        prompt = "Say hello once",
        schedule = new { at = DateTimeOffset.UtcNow.AddHours(1).ToString("o") }
    };

    [Fact]
    public async Task ListTasks_ReturnsOkWithArray()
    {
        var client = CreateAuthenticatedClient();

        var response = await client.GetAsync("/api/scheduled-tasks");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(JsonValueKind.Array, body.ValueKind);
    }

    [Fact]
    public async Task CreateTask_ValidCron_ReturnsCreated()
    {
        var client = CreateAuthenticatedClient();
        var taskId = Guid.NewGuid().ToString();

        var response = await client.PostAsJsonAsync("/api/scheduled-tasks", MakeCronTask(taskId));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(taskId, body.GetProperty("id").GetString());
        Assert.Equal("Test cron task", body.GetProperty("name").GetString());
        Assert.True(body.GetProperty("state").GetProperty("nextRunAt").GetString() is not null,
            "nextRunAt should be non-null for a valid cron task");
    }

    [Fact]
    public async Task CreateTask_InvalidSchedule_ReturnsBadRequest()
    {
        var client = CreateAuthenticatedClient();

        var payload = new
        {
            id = Guid.NewGuid().ToString(),
            name = "Bad task",
            prompt = "Hello",
            schedule = new { } // No cron, intervalMs, or at — invalid
        };

        var response = await client.PostAsJsonAsync("/api/scheduled-tasks", payload);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task GetTask_NotFound_Returns404()
    {
        var client = CreateAuthenticatedClient();
        var fakeId = Guid.NewGuid().ToString();

        var response = await client.GetAsync($"/api/scheduled-tasks/{fakeId}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task DeleteTask_Exists_ReturnsNoContent()
    {
        var client = CreateAuthenticatedClient();
        var taskId = Guid.NewGuid().ToString();

        // Create the task first
        var createResponse = await client.PostAsJsonAsync("/api/scheduled-tasks", MakeCronTask(taskId));
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);

        // Delete it
        var deleteResponse = await client.DeleteAsync($"/api/scheduled-tasks/{taskId}");
        Assert.Equal(HttpStatusCode.NoContent, deleteResponse.StatusCode);

        // Verify it's gone
        var getResponse = await client.GetAsync($"/api/scheduled-tasks/{taskId}");
        Assert.Equal(HttpStatusCode.NotFound, getResponse.StatusCode);
    }

    [Fact]
    public async Task CreateTask_OneShot_ReturnsCreated()
    {
        var client = CreateAuthenticatedClient();
        var taskId = Guid.NewGuid().ToString();

        var response = await client.PostAsJsonAsync("/api/scheduled-tasks", MakeOneShotTask(taskId));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(taskId, body.GetProperty("id").GetString());
        Assert.Equal("Test one-shot task", body.GetProperty("name").GetString());
    }

    [Fact]
    public async Task Trigger_WithBody_ReturnsOk()
    {
        var client = CreateAuthenticatedClient();
        var taskId = Guid.NewGuid().ToString();

        // Create a task
        var createResponse = await client.PostAsJsonAsync("/api/scheduled-tasks", MakeCronTask(taskId));
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);

        // Trigger it with a JSON body
        var triggerPayload = new { context = "some webhook data" };
        var triggerResponse = await client.PostAsJsonAsync($"/api/scheduled-tasks/{taskId}/trigger", triggerPayload);

        Assert.Equal(HttpStatusCode.OK, triggerResponse.StatusCode);

        var body = await triggerResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("triggered", body.GetProperty("status").GetString());
        Assert.Equal(taskId, body.GetProperty("taskId").GetString());
    }

    private sealed class FakeTextProvider : ILlmTextProvider
    {
        public string Key => "text-provider";
        public IReadOnlyList<ProviderConfigField> ConfigFields => [];
        public void Configure(JsonElement configuration) { }
        public int? GetContextWindow(string model) => null;

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
