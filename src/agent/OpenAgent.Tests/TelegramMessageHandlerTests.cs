using OpenAgent.Channel.Telegram;
using OpenAgent.Models.Configs;
using OpenAgent.Tests.Fakes;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace OpenAgent.Tests;

public class TelegramMessageHandlerTests
{
    private const long AllowedUserId = 42;
    private const long BlockedUserId = 99999;
    private const long ChatId = 12345;
    private const string ConnectionId = "test-connection-1";

    private static readonly AgentConfig TestAgentConfig = new()
    {
        TextProvider = "azure-openai-text",
        TextModel = "gpt-5.2-chat"
    };

    private static TelegramOptions CreateOptions(params long[] allowedUserIds) => new()
    {
        BotToken = "fake-token",
        AllowedUserIds = [..allowedUserIds]
    };

    private static TelegramOptions CreateBatchOptions(params long[] allowedUserIds) => new()
    {
        BotToken = "fake-token",
        AllowedUserIds = [..allowedUserIds],
        StreamResponses = false
    };

    private static TelegramOptions CreateStreamingOptions(params long[] allowedUserIds) => new()
    {
        BotToken = "fake-token",
        AllowedUserIds = [..allowedUserIds],
        StreamResponses = true
    };

    private static Update CreatePrivateTextUpdate(long userId, long chatId, string text) => new()
    {
        Message = new Message
        {
            Text = text,
            From = new User { Id = userId, IsBot = false, FirstName = "Test" },
            Chat = new Chat { Id = chatId, Type = ChatType.Private },
            Date = DateTime.UtcNow
        }
    };

    [Fact]
    public async Task HandleUpdateAsync_ValidMessage_SendsLlmReply()
    {
        var store = new InMemoryConversationStore();
        var provider = new FakeTelegramTextProvider("Hello from LLM");
        var handler = new TelegramMessageHandler(store, new FakeConnectionStore(ConnectionId), _ => provider, ConnectionId, TestAgentConfig, CreateOptions(AllowedUserId));
        var sender = new FakeTelegramSender();
        var update = CreatePrivateTextUpdate(AllowedUserId, ChatId, "Hi");

        await handler.HandleUpdateAsync(sender, update, CancellationToken.None);

        Assert.Single(sender.TypingCalls);
        Assert.Equal(ChatId, sender.TypingCalls[0].ChatId);
        Assert.Single(sender.HtmlCalls);
        Assert.Contains("Hello from LLM", sender.HtmlCalls[0].Html);
    }

    [Fact]
    public async Task HandleUpdateAsync_ValidMessage_CreatesConversation()
    {
        var store = new InMemoryConversationStore();
        var provider = new FakeTelegramTextProvider("reply");
        var handler = new TelegramMessageHandler(store, new FakeConnectionStore(ConnectionId), _ => provider, ConnectionId, TestAgentConfig, CreateOptions(AllowedUserId));
        var sender = new FakeTelegramSender();
        var update = CreatePrivateTextUpdate(AllowedUserId, ChatId, "Hi");

        await handler.HandleUpdateAsync(sender, update, CancellationToken.None);

        var expectedId = $"telegram:{ConnectionId}:{ChatId}";
        var conversation = store.Get(expectedId);
        Assert.NotNull(conversation);
        Assert.Equal("telegram", conversation.Source);
        Assert.Equal(Models.Conversations.ConversationType.Text, conversation.Type);
    }

    [Fact]
    public async Task HandleUpdateAsync_NewConversationsDisabled_DropsMessage()
    {
        var store = new InMemoryConversationStore();
        var provider = new FakeTelegramTextProvider("should not see this");
        var connStore = new FakeConnectionStore(ConnectionId, allowNewConversations: false);
        var handler = new TelegramMessageHandler(store, connStore, _ => provider, ConnectionId, TestAgentConfig, CreateOptions(AllowedUserId));
        var sender = new FakeTelegramSender();
        var update = CreatePrivateTextUpdate(AllowedUserId, ChatId, "Hi");

        await handler.HandleUpdateAsync(sender, update, CancellationToken.None);

        Assert.Empty(sender.TypingCalls);
        Assert.Empty(sender.HtmlCalls);
        Assert.Empty(sender.TextCalls);
    }

    [Fact]
    public async Task HandleUpdateAsync_NullMessage_IgnoresUpdate()
    {
        var store = new InMemoryConversationStore();
        var provider = new FakeTelegramTextProvider("reply");
        var handler = new TelegramMessageHandler(store, new FakeConnectionStore(ConnectionId), _ => provider, ConnectionId, TestAgentConfig, CreateOptions(AllowedUserId));
        var sender = new FakeTelegramSender();
        var update = new Update();

        await handler.HandleUpdateAsync(sender, update, CancellationToken.None);

        Assert.Empty(sender.TypingCalls);
    }

    [Fact]
    public async Task HandleUpdateAsync_GroupChat_CreatesGroupConversation()
    {
        var store = new InMemoryConversationStore();
        var provider = new FakeTelegramTextProvider("reply");
        var handler = new TelegramMessageHandler(store, new FakeConnectionStore(ConnectionId), _ => provider, ConnectionId, TestAgentConfig, CreateOptions(AllowedUserId));
        var sender = new FakeTelegramSender();
        var groupChatId = 99999L;
        var update = new Update
        {
            Message = new Message
            {
                Text = "Hi group",
                From = new User { Id = AllowedUserId, IsBot = false, FirstName = "Test" },
                Chat = new Chat { Id = groupChatId, Type = ChatType.Group },
                Date = DateTime.UtcNow
            }
        };

        await handler.HandleUpdateAsync(sender, update, CancellationToken.None);

        var expectedId = $"telegram:{ConnectionId}:{groupChatId}";
        var conversation = store.Get(expectedId);
        Assert.NotNull(conversation);
    }

    [Fact]
    public async Task HandleUpdateAsync_NullText_IgnoresMessage()
    {
        var store = new InMemoryConversationStore();
        var provider = new FakeTelegramTextProvider("reply");
        var handler = new TelegramMessageHandler(store, new FakeConnectionStore(ConnectionId), _ => provider, ConnectionId, TestAgentConfig, CreateOptions(AllowedUserId));
        var sender = new FakeTelegramSender();
        var update = new Update
        {
            Message = new Message
            {
                Text = null,
                From = new User { Id = AllowedUserId, IsBot = false, FirstName = "Test" },
                Chat = new Chat { Id = ChatId, Type = ChatType.Private },
                Date = DateTime.UtcNow
            }
        };

        await handler.HandleUpdateAsync(sender, update, CancellationToken.None);

        Assert.Empty(sender.TypingCalls);
    }

    [Fact]
    public async Task HandleUpdateAsync_LlmThrows_SendsErrorMessage()
    {
        var store = new InMemoryConversationStore();
        var provider = new ThrowingTextProvider();
        var handler = new TelegramMessageHandler(store, new FakeConnectionStore(ConnectionId), _ => provider, ConnectionId, TestAgentConfig, CreateOptions(AllowedUserId));
        var sender = new FakeTelegramSender();
        var update = CreatePrivateTextUpdate(AllowedUserId, ChatId, "Hi");

        await handler.HandleUpdateAsync(sender, update, CancellationToken.None);

        Assert.Single(sender.HtmlCalls);
        Assert.Contains("went wrong", sender.HtmlCalls[0].Html);
    }

    [Fact]
    public async Task HandleUpdateAsync_HtmlFails_FallsBackToPlainText()
    {
        var store = new InMemoryConversationStore();
        var provider = new FakeTelegramTextProvider("Hello **bold**");
        var handler = new TelegramMessageHandler(store, new FakeConnectionStore(ConnectionId), _ => provider, ConnectionId, TestAgentConfig, CreateOptions(AllowedUserId));
        var sender = new FakeTelegramSender { FailHtml = true };
        var update = CreatePrivateTextUpdate(AllowedUserId, ChatId, "Hi");

        await handler.HandleUpdateAsync(sender, update, CancellationToken.None);

        Assert.Single(sender.TextCalls);
        Assert.Equal(ChatId, sender.TextCalls[0].ChatId);
        Assert.Contains("Hello", sender.TextCalls[0].Text);
    }

    [Fact]
    public async Task HandleUpdateAsync_EmptyAllowList_AllowsEveryone()
    {
        var store = new InMemoryConversationStore();
        var provider = new FakeTelegramTextProvider("reply");
        var handler = new TelegramMessageHandler(store, new FakeConnectionStore(ConnectionId), _ => provider, ConnectionId, TestAgentConfig, CreateOptions());
        var sender = new FakeTelegramSender();
        var update = CreatePrivateTextUpdate(AllowedUserId, ChatId, "Hi");

        await handler.HandleUpdateAsync(sender, update, CancellationToken.None);

        Assert.NotEmpty(sender.TypingCalls);
        Assert.NotEmpty(sender.HtmlCalls);
    }

    [Fact]
    public async Task HandleUpdateAsync_BatchMode_SendsFinalMessageOnly()
    {
        var store = new InMemoryConversationStore();
        var provider = new StreamingTextProvider("Hello", " ", "world");
        var handler = new TelegramMessageHandler(store, new FakeConnectionStore(ConnectionId), _ => provider, ConnectionId, TestAgentConfig, CreateBatchOptions(AllowedUserId));
        var sender = new FakeTelegramSender();
        var update = CreatePrivateTextUpdate(AllowedUserId, ChatId, "Hi");

        await handler.HandleUpdateAsync(sender, update, CancellationToken.None);

        Assert.Empty(sender.DraftCalls);
        Assert.Single(sender.HtmlCalls);
        Assert.Contains("Hello world", sender.HtmlCalls[0].Html);
    }

    [Fact]
    public async Task HandleUpdateAsync_StreamMode_SendsDraftsAndFinalMessage()
    {
        var store = new InMemoryConversationStore();
        var provider = new StreamingTextProvider("Hello", " ", "world");
        // Zero throttle so drafts fire on every token
        var handler = new TelegramMessageHandler(store, new FakeConnectionStore(ConnectionId), _ => provider, ConnectionId, TestAgentConfig, CreateStreamingOptions(AllowedUserId));
        var sender = new FakeTelegramSender();
        var update = CreatePrivateTextUpdate(AllowedUserId, ChatId, "Hi");

        await handler.HandleUpdateAsync(sender, update, CancellationToken.None);

        // Drafts were sent (at least one)
        Assert.NotEmpty(sender.DraftCalls);
        // All drafts target the same chat
        Assert.All(sender.DraftCalls, d => Assert.Equal(ChatId, d.ChatId));
        // All drafts share the same draft ID
        var draftId = sender.DraftCalls[0].DraftId;
        Assert.All(sender.DraftCalls, d => Assert.Equal(draftId, d.DraftId));
        // Final message sent via HTML
        Assert.Single(sender.HtmlCalls);
        Assert.Contains("Hello world", sender.HtmlCalls[0].Html);
    }

    [Fact]
    public async Task HandleUpdateAsync_StreamMode_DraftFailure_StillSendsFinalMessage()
    {
        var store = new InMemoryConversationStore();
        var provider = new StreamingTextProvider("Hello", " ", "world");
        var handler = new TelegramMessageHandler(store, new FakeConnectionStore(ConnectionId), _ => provider, ConnectionId, TestAgentConfig, CreateStreamingOptions(AllowedUserId));
        var sender = new FakeTelegramSender { FailDraft = true };
        var update = CreatePrivateTextUpdate(AllowedUserId, ChatId, "Hi");

        await handler.HandleUpdateAsync(sender, update, CancellationToken.None);

        // Drafts all failed, but final message still sent
        Assert.Empty(sender.DraftCalls);
        Assert.Single(sender.HtmlCalls);
        Assert.Contains("Hello world", sender.HtmlCalls[0].Html);
    }

    [Fact]
    public async Task HandleUpdateAsync_StreamMode_LlmThrows_SendsErrorMessage()
    {
        var store = new InMemoryConversationStore();
        var provider = new ThrowingTextProvider();
        var handler = new TelegramMessageHandler(store, new FakeConnectionStore(ConnectionId), _ => provider, ConnectionId, TestAgentConfig, CreateStreamingOptions(AllowedUserId));
        var sender = new FakeTelegramSender();
        var update = CreatePrivateTextUpdate(AllowedUserId, ChatId, "Hi");

        await handler.HandleUpdateAsync(sender, update, CancellationToken.None);

        Assert.Single(sender.HtmlCalls);
        Assert.Contains("went wrong", sender.HtmlCalls[0].Html);
    }
}
