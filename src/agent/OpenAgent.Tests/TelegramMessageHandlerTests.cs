using OpenAgent.Channel.Telegram;
using OpenAgent.Tests.Fakes;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace OpenAgent.Tests;

public class TelegramMessageHandlerTests
{
    private const long AllowedUserId = 12345;
    private const long BlockedUserId = 99999;
    private const long ChatId = 12345;

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
        // Arrange
        var store = new InMemoryConversationStore();
        var provider = new FakeTelegramTextProvider("Hello from LLM");
        var handler = new TelegramMessageHandler(store, provider, CreateOptions(AllowedUserId));
        var sender = new FakeTelegramSender();
        var update = CreatePrivateTextUpdate(AllowedUserId, ChatId, "Hi");

        // Act
        await handler.HandleUpdateAsync(sender, update, CancellationToken.None);

        // Assert — typing indicator sent
        Assert.Single(sender.TypingCalls);
        Assert.Equal(ChatId, sender.TypingCalls[0].ChatId);

        // Assert — reply sent as HTML
        Assert.Single(sender.HtmlCalls);
        Assert.Equal(ChatId, sender.HtmlCalls[0].ChatId);
        Assert.Contains("Hello from LLM", sender.HtmlCalls[0].Html);
    }

    [Fact]
    public async Task HandleUpdateAsync_ValidMessage_CreatesConversation()
    {
        // Arrange
        var store = new InMemoryConversationStore();
        var provider = new FakeTelegramTextProvider("reply");
        var handler = new TelegramMessageHandler(store, provider, CreateOptions(AllowedUserId));
        var sender = new FakeTelegramSender();
        var update = CreatePrivateTextUpdate(AllowedUserId, ChatId, "Hi");

        // Act
        await handler.HandleUpdateAsync(sender, update, CancellationToken.None);

        // Assert — conversation created with correct ID and source
        var conversation = store.Get($"telegram-{ChatId}");
        Assert.NotNull(conversation);
        Assert.Equal("telegram", conversation.Source);
        Assert.Equal(Models.Conversations.ConversationType.Text, conversation.Type);
    }

    [Fact]
    public async Task HandleUpdateAsync_BlockedUser_IgnoresMessage()
    {
        // Arrange
        var store = new InMemoryConversationStore();
        var provider = new FakeTelegramTextProvider("should not see this");
        var handler = new TelegramMessageHandler(store, provider, CreateOptions(AllowedUserId));
        var sender = new FakeTelegramSender();
        var update = CreatePrivateTextUpdate(BlockedUserId, ChatId, "Hi");

        // Act
        await handler.HandleUpdateAsync(sender, update, CancellationToken.None);

        // Assert — nothing sent
        Assert.Empty(sender.TypingCalls);
        Assert.Empty(sender.HtmlCalls);
        Assert.Empty(sender.TextCalls);
    }

    [Fact]
    public async Task HandleUpdateAsync_NullMessage_IgnoresUpdate()
    {
        // Arrange
        var store = new InMemoryConversationStore();
        var provider = new FakeTelegramTextProvider("reply");
        var handler = new TelegramMessageHandler(store, provider, CreateOptions(AllowedUserId));
        var sender = new FakeTelegramSender();
        var update = new Update(); // no Message

        // Act
        await handler.HandleUpdateAsync(sender, update, CancellationToken.None);

        // Assert
        Assert.Empty(sender.TypingCalls);
    }

    [Fact]
    public async Task HandleUpdateAsync_GroupChat_IgnoresMessage()
    {
        // Arrange
        var store = new InMemoryConversationStore();
        var provider = new FakeTelegramTextProvider("reply");
        var handler = new TelegramMessageHandler(store, provider, CreateOptions(AllowedUserId));
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

        // Act
        await handler.HandleUpdateAsync(sender, update, CancellationToken.None);

        // Assert
        Assert.Empty(sender.TypingCalls);
    }

    [Fact]
    public async Task HandleUpdateAsync_NullText_IgnoresMessage()
    {
        // Arrange
        var store = new InMemoryConversationStore();
        var provider = new FakeTelegramTextProvider("reply");
        var handler = new TelegramMessageHandler(store, provider, CreateOptions(AllowedUserId));
        var sender = new FakeTelegramSender();
        var update = new Update
        {
            Message = new Message
            {
                Text = null, // photo, sticker, etc.
                From = new User { Id = AllowedUserId, IsBot = false, FirstName = "Test" },
                Chat = new Chat { Id = ChatId, Type = ChatType.Private },
                Date = DateTime.UtcNow
            }
        };

        // Act
        await handler.HandleUpdateAsync(sender, update, CancellationToken.None);

        // Assert
        Assert.Empty(sender.TypingCalls);
    }

    [Fact]
    public async Task HandleUpdateAsync_LlmThrows_SendsErrorMessage()
    {
        // Arrange
        var store = new InMemoryConversationStore();
        var provider = new ThrowingTextProvider();
        var handler = new TelegramMessageHandler(store, provider, CreateOptions(AllowedUserId));
        var sender = new FakeTelegramSender();
        var update = CreatePrivateTextUpdate(AllowedUserId, ChatId, "Hi");

        // Act
        await handler.HandleUpdateAsync(sender, update, CancellationToken.None);

        // Assert — error message sent
        Assert.Single(sender.HtmlCalls);
        Assert.Contains("process", sender.HtmlCalls[0].Html);
    }

    [Fact]
    public async Task HandleUpdateAsync_HtmlFails_FallsBackToPlainText()
    {
        // Arrange
        var store = new InMemoryConversationStore();
        var provider = new FakeTelegramTextProvider("Hello **bold**");
        var handler = new TelegramMessageHandler(store, provider, CreateOptions(AllowedUserId));
        var sender = new FakeTelegramSender { FailHtml = true };
        var update = CreatePrivateTextUpdate(AllowedUserId, ChatId, "Hi");

        // Act
        await handler.HandleUpdateAsync(sender, update, CancellationToken.None);

        // Assert — fell back to plain text
        Assert.Single(sender.TextCalls);
        Assert.Equal(ChatId, sender.TextCalls[0].ChatId);
        Assert.Contains("Hello", sender.TextCalls[0].Text);
    }

    [Fact]
    public async Task HandleUpdateAsync_EmptyAllowList_BlocksEveryone()
    {
        // Arrange — no allowed users
        var store = new InMemoryConversationStore();
        var provider = new FakeTelegramTextProvider("reply");
        var handler = new TelegramMessageHandler(store, provider, CreateOptions());
        var sender = new FakeTelegramSender();
        var update = CreatePrivateTextUpdate(AllowedUserId, ChatId, "Hi");

        // Act
        await handler.HandleUpdateAsync(sender, update, CancellationToken.None);

        // Assert
        Assert.Empty(sender.TypingCalls);
        Assert.Empty(sender.HtmlCalls);
    }
}
