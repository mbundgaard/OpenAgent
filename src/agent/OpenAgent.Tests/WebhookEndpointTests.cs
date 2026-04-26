using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OpenAgent.Contracts;
using OpenAgent.Models.Common;
using OpenAgent.Models.Conversations;
using OpenAgent.Models.Providers;

namespace OpenAgent.Tests;

public class WebhookEndpointTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly CapturingTextProvider _capturingProvider;

    public WebhookEndpointTests(WebApplicationFactory<Program> factory)
    {
        TestSetup.EnsureConfigSeeded();
        _capturingProvider = new CapturingTextProvider();
        _factory = factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                services.RemoveAll(typeof(ILlmTextProvider));
                services.AddKeyedSingleton<ILlmTextProvider>("azure-openai-text", _capturingProvider);
                services.AddSingleton<ILlmTextProvider>(_capturingProvider);
            });
        });
    }

    [Fact]
    public async Task PostWebhook_EmptyBody_Returns400()
    {
        // Pre-create a conversation so we're sure 400 is about the body, not the conversation
        var store = _factory.Services.GetRequiredService<IConversationStore>();
        var conversationId = Guid.NewGuid().ToString();
        store.GetOrCreate(conversationId, "app", "azure-openai-text", "test-model");

        var client = _factory.CreateClient();
        var response = await client.PostAsync(
            $"/api/webhook/conversation/{conversationId}",
            new StringContent("", Encoding.UTF8, "text/plain"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task PostWebhook_UnknownConversationId_Returns404AndDoesNotCreate()
    {
        var client = _factory.CreateClient();
        var unknownId = Guid.NewGuid().ToString();

        var response = await client.PostAsync(
            $"/api/webhook/conversation/{unknownId}",
            new StringContent("some event", Encoding.UTF8, "text/plain"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        // Verify auto-create did NOT happen
        var store = _factory.Services.GetRequiredService<IConversationStore>();
        var conv = store.Get(unknownId);
        Assert.Null(conv);
    }

    [Fact]
    public async Task PostWebhook_ValidBody_Returns202AndTriggersCompletion()
    {
        var store = _factory.Services.GetRequiredService<IConversationStore>();
        var conversationId = Guid.NewGuid().ToString();
        store.GetOrCreate(conversationId, "app", "azure-openai-text", "test-model");

        var client = _factory.CreateClient();
        var response = await client.PostAsync(
            $"/api/webhook/conversation/{conversationId}",
            new StringContent("new episode added: Foo S01E02", Encoding.UTF8, "text/plain"));

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        // Fire-and-forget: poll briefly until the background task captures the call
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (_capturingProvider.CallCount == 0 && DateTime.UtcNow < deadline)
            await Task.Delay(25);

        Assert.Equal(1, _capturingProvider.CallCount);
        Assert.NotNull(_capturingProvider.LastConversation);
        Assert.Equal(conversationId, _capturingProvider.LastConversation!.Id);
        Assert.NotNull(_capturingProvider.LastUserMessage);
        Assert.Equal("user", _capturingProvider.LastUserMessage!.Role);
        Assert.Equal("new episode added: Foo S01E02", _capturingProvider.LastUserMessage!.Content);
    }

    [Fact]
    public async Task PostWebhook_NoMentionMatch_DropsAndDoesNotTriggerCompletion()
    {
        var store = _factory.Services.GetRequiredService<IConversationStore>();
        var conversationId = Guid.NewGuid().ToString();
        var conv = store.GetOrCreate(conversationId, "app", "azure-openai-text", "test-model");
        conv.MentionFilter = ["Dex"];
        store.Update(conv);

        var callCountBefore = _capturingProvider.CallCount;

        var client = _factory.CreateClient();
        var response = await client.PostAsync(
            $"/api/webhook/conversation/{conversationId}",
            new StringContent("hello there", Encoding.UTF8, "text/plain"));

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        // Give the background task a chance to (incorrectly) run.
        await Task.Delay(200);
        Assert.Equal(callCountBefore, _capturingProvider.CallCount);
    }

    private sealed class CapturingTextProvider : ILlmTextProvider
    {
        private int _callCount;

        public Conversation? LastConversation { get; private set; }
        public Message? LastUserMessage { get; private set; }
        public int CallCount => _callCount;

        public string Key => "text-provider";
        public IReadOnlyList<ProviderConfigField> ConfigFields => [];
        public void Configure(JsonElement configuration) { }
        public int? GetContextWindow(string model) => null;

        public async IAsyncEnumerable<CompletionEvent> CompleteAsync(Conversation conversation, Message userMessage,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            LastConversation = conversation;
            LastUserMessage = userMessage;
            Interlocked.Increment(ref _callCount);
            yield return new TextDelta("ok");
            await Task.CompletedTask;
        }

        public async IAsyncEnumerable<CompletionEvent> CompleteAsync(IReadOnlyList<Message> messages, string model,
            CompletionOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            yield return new TextDelta("raw");
            await Task.CompletedTask;
        }
    }
}
