using System.Runtime.CompilerServices;
using System.Text.Json;
using OpenAgent.Channel.WhatsApp;
using OpenAgent.Contracts;
using OpenAgent.Models.Common;
using OpenAgent.Models.Conversations;
using OpenAgent.Models.Providers;
using OpenAgent.Tests.Fakes;

namespace OpenAgent.Tests;

public class WhatsAppMessageHandlerTests
{
    private const string AllowedDmChatId = "+4512345678@s.whatsapp.net";
    private const string BlockedChatId = "+4599999999@s.whatsapp.net";
    private const string GroupChatId = "120363001234567890@g.us";
    private const string ConnectionId = "wa-conn-1";

    private static WhatsAppOptions CreateOptions(params string[] allowedChatIds) => new()
    {
        AllowedChatIds = [..allowedChatIds]
    };

    private static NodeEvent CreateTextMessage(string chatId, string text, string? pushName = "Alice", string? messageId = null) => new()
    {
        Type = "message",
        Id = messageId ?? Guid.NewGuid().ToString(),
        ChatId = chatId,
        From = chatId,
        PushName = pushName,
        Text = text,
        Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
    };

    [Fact]
    public async Task AllowedDm_SendsLlmReply()
    {
        var store = new InMemoryConversationStore();
        var provider = new FakeTelegramTextProvider("Hello from LLM");
        var options = CreateOptions(AllowedDmChatId);
        var handler = new WhatsAppMessageHandler(store, provider, ConnectionId, "azure-openai-text", "gpt-5.2-chat", options);
        var sender = new FakeWhatsAppSender();
        var message = CreateTextMessage(AllowedDmChatId, "Hi");

        await handler.HandleMessageAsync(sender, message, CancellationToken.None);

        // Composing indicator sent
        Assert.Single(sender.ComposingCalls);
        Assert.Equal(AllowedDmChatId, sender.ComposingCalls[0]);

        // LLM reply sent
        Assert.Single(sender.TextCalls);
        Assert.Contains("Hello from LLM", sender.TextCalls[0].Text);
    }

    [Fact]
    public async Task BlockedChat_IgnoresMessage()
    {
        var store = new InMemoryConversationStore();
        var provider = new FakeTelegramTextProvider("should not see this");
        var options = CreateOptions(AllowedDmChatId);
        var handler = new WhatsAppMessageHandler(store, provider, ConnectionId, "azure-openai-text", "gpt-5.2-chat", options);
        var sender = new FakeWhatsAppSender();
        var message = CreateTextMessage(BlockedChatId, "Hi");

        await handler.HandleMessageAsync(sender, message, CancellationToken.None);

        Assert.Empty(sender.ComposingCalls);
        Assert.Empty(sender.TextCalls);
    }

    [Fact]
    public async Task EmptyAllowList_AllowsEveryone()
    {
        var store = new InMemoryConversationStore();
        var provider = new FakeTelegramTextProvider("reply");
        var options = CreateOptions(); // empty = allow all
        var handler = new WhatsAppMessageHandler(store, provider, ConnectionId, "azure-openai-text", "gpt-5.2-chat", options);
        var sender = new FakeWhatsAppSender();
        var message = CreateTextMessage(AllowedDmChatId, "Hi");

        await handler.HandleMessageAsync(sender, message, CancellationToken.None);

        Assert.NotEmpty(sender.ComposingCalls);
        Assert.NotEmpty(sender.TextCalls);
    }

    [Fact]
    public async Task GroupMessage_PrefixesSenderName()
    {
        var store = new InMemoryConversationStore();
        var provider = new CapturingTextProvider("reply");
        var options = CreateOptions(GroupChatId);
        var handler = new WhatsAppMessageHandler(store, provider, ConnectionId, "azure-openai-text", "gpt-5.2-chat", options);
        var sender = new FakeWhatsAppSender();
        var message = CreateTextMessage(GroupChatId, "Hello group", pushName: "Bob");

        await handler.HandleMessageAsync(sender, message, CancellationToken.None);

        // Verify the user message passed to the provider has the [PushName] prefix
        Assert.Single(provider.CapturedMessages);
        Assert.StartsWith("[Bob] ", provider.CapturedMessages[0].Content!);
    }

    [Fact]
    public async Task DuplicateMessage_IgnoredSecondTime()
    {
        var store = new InMemoryConversationStore();
        var provider = new StreamingTextProvider("Hello");
        var options = CreateOptions(AllowedDmChatId);
        var handler = new WhatsAppMessageHandler(store, provider, ConnectionId, "azure-openai-text", "gpt-5.2-chat", options);
        var sender = new FakeWhatsAppSender();
        var messageId = "msg-duplicate-123";
        var message = CreateTextMessage(AllowedDmChatId, "Hi", messageId: messageId);

        // First call should succeed
        await handler.HandleMessageAsync(sender, message, CancellationToken.None);
        Assert.Single(sender.TextCalls);

        // Second call with same message ID should be ignored
        await handler.HandleMessageAsync(sender, message, CancellationToken.None);
        Assert.Single(sender.TextCalls); // still just one
    }

    [Fact]
    public async Task LlmError_SendsErrorMessage()
    {
        var store = new InMemoryConversationStore();
        var provider = new ThrowingTextProvider();
        var options = CreateOptions(AllowedDmChatId);
        var handler = new WhatsAppMessageHandler(store, provider, ConnectionId, "azure-openai-text", "gpt-5.2-chat", options);
        var sender = new FakeWhatsAppSender();
        var message = CreateTextMessage(AllowedDmChatId, "Hi");

        await handler.HandleMessageAsync(sender, message, CancellationToken.None);

        Assert.Single(sender.TextCalls);
        Assert.Contains("went wrong", sender.TextCalls[0].Text);
    }

    [Fact]
    public async Task CreatesConversation_WithDerivedId()
    {
        var store = new InMemoryConversationStore();
        var provider = new FakeTelegramTextProvider("reply");
        var options = CreateOptions(AllowedDmChatId);
        var handler = new WhatsAppMessageHandler(store, provider, ConnectionId, "azure-openai-text", "gpt-5.2-chat", options);
        var sender = new FakeWhatsAppSender();
        var message = CreateTextMessage(AllowedDmChatId, "Hi");

        await handler.HandleMessageAsync(sender, message, CancellationToken.None);

        var expectedId = $"whatsapp:{ConnectionId}:{AllowedDmChatId}";
        var conversation = store.Get(expectedId);
        Assert.NotNull(conversation);
        Assert.Equal("whatsapp", conversation.Source);
        Assert.Equal(Models.Conversations.ConversationType.Text, conversation.Type);
    }

    /// <summary>
    /// Fake text provider that captures the user message passed to CompleteAsync.
    /// </summary>
    private sealed class CapturingTextProvider : ILlmTextProvider
    {
        private readonly string _response;
        public List<Message> CapturedMessages { get; } = [];

        public CapturingTextProvider(string response) => _response = response;

        public string Key => "capturing-text";
        public IReadOnlyList<ProviderConfigField> ConfigFields => [];
        public void Configure(JsonElement configuration) { }

        public async IAsyncEnumerable<CompletionEvent> CompleteAsync(
            Conversation conversation,
            Message userMessage,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            CapturedMessages.Add(userMessage);
            yield return new TextDelta(_response);
            await Task.CompletedTask;
        }

        public async IAsyncEnumerable<CompletionEvent> CompleteAsync(
            IReadOnlyList<Message> messages,
            string model,
            CompletionOptions? options = null,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            yield return new TextDelta(_response);
            await Task.CompletedTask;
        }
    }
}
