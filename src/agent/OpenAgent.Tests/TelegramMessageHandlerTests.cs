using OpenAgent.Channel.Telegram;
using OpenAgent.Tests.Fakes;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace OpenAgent.Tests;

public class TelegramMessageHandlerTests
{
    private const long AllowedUserId = 42;
    private const long BlockedUserId = 99999;
    private const long ChatId = 12345;
    private const string ConversationId = "test-conversation-1";

    private static TelegramOptions CreateOptions(params long[] allowedUserIds) => new()
    {
        BotToken = "fake-token",
        AllowedUserIds = [..allowedUserIds]
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
        var handler = new TelegramMessageHandler(store, provider, ConversationId, CreateOptions(AllowedUserId));
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
        var handler = new TelegramMessageHandler(store, provider, ConversationId, CreateOptions(AllowedUserId));
        var sender = new FakeTelegramSender();
        var update = CreatePrivateTextUpdate(AllowedUserId, ChatId, "Hi");

        await handler.HandleUpdateAsync(sender, update, CancellationToken.None);

        var conversation = store.Get(ConversationId);
        Assert.NotNull(conversation);
        Assert.Equal("telegram", conversation.Source);
        Assert.Equal(Models.Conversations.ConversationType.Text, conversation.Type);
    }

    [Fact]
    public async Task HandleUpdateAsync_BlockedUser_IgnoresMessage()
    {
        var store = new InMemoryConversationStore();
        var provider = new FakeTelegramTextProvider("should not see this");
        var handler = new TelegramMessageHandler(store, provider, ConversationId, CreateOptions(AllowedUserId));
        var sender = new FakeTelegramSender();
        var update = CreatePrivateTextUpdate(BlockedUserId, ChatId, "Hi");

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
        var handler = new TelegramMessageHandler(store, provider, ConversationId, CreateOptions(AllowedUserId));
        var sender = new FakeTelegramSender();
        var update = new Update();

        await handler.HandleUpdateAsync(sender, update, CancellationToken.None);

        Assert.Empty(sender.TypingCalls);
    }

    [Fact]
    public async Task HandleUpdateAsync_GroupChat_IgnoresMessage()
    {
        var store = new InMemoryConversationStore();
        var provider = new FakeTelegramTextProvider("reply");
        var handler = new TelegramMessageHandler(store, provider, ConversationId, CreateOptions(AllowedUserId));
        var sender = new FakeTelegramSender();
        var update = new Update
        {
            Message = new Message
            {
                Text = "Hi",
                From = new User { Id = AllowedUserId, IsBot = false, FirstName = "Test" },
                Chat = new Chat { Id = ChatId, Type = ChatType.Group },
                Date = DateTime.UtcNow
            }
        };

        await handler.HandleUpdateAsync(sender, update, CancellationToken.None);

        Assert.Empty(sender.TypingCalls);
    }

    [Fact]
    public async Task HandleUpdateAsync_NullText_IgnoresMessage()
    {
        var store = new InMemoryConversationStore();
        var provider = new FakeTelegramTextProvider("reply");
        var handler = new TelegramMessageHandler(store, provider, ConversationId, CreateOptions(AllowedUserId));
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
        var handler = new TelegramMessageHandler(store, provider, ConversationId, CreateOptions(AllowedUserId));
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
        var handler = new TelegramMessageHandler(store, provider, ConversationId, CreateOptions(AllowedUserId));
        var sender = new FakeTelegramSender { FailHtml = true };
        var update = CreatePrivateTextUpdate(AllowedUserId, ChatId, "Hi");

        await handler.HandleUpdateAsync(sender, update, CancellationToken.None);

        Assert.Single(sender.TextCalls);
        Assert.Equal(ChatId, sender.TextCalls[0].ChatId);
        Assert.Contains("Hello", sender.TextCalls[0].Text);
    }

    [Fact]
    public async Task HandleUpdateAsync_EmptyAllowList_BlocksEveryone()
    {
        var store = new InMemoryConversationStore();
        var provider = new FakeTelegramTextProvider("reply");
        var handler = new TelegramMessageHandler(store, provider, ConversationId, CreateOptions());
        var sender = new FakeTelegramSender();
        var update = CreatePrivateTextUpdate(AllowedUserId, ChatId, "Hi");

        await handler.HandleUpdateAsync(sender, update, CancellationToken.None);

        Assert.Empty(sender.TypingCalls);
        Assert.Empty(sender.HtmlCalls);
    }
}
