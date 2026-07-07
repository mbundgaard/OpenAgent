using System.Text.Json;
using OpenAgent.Channel.Telegram;
using OpenAgent.Models.Connections;

namespace OpenAgent.Tests;

public class TelegramChannelProviderFactoryTests
{
    private static Connection ConnectionWithConfig(string json) => new()
    {
        Id = "conn-1",
        Name = "Test",
        Type = "telegram",
        ConversationId = "conv-1",
        Config = JsonDocument.Parse(json).RootElement.Clone()
    };

    [Fact]
    public void ParseOptions_RichMessagesFalseString_DisablesRichMessages()
    {
        var connection = ConnectionWithConfig("""{"botToken":"t","richMessages":"false"}""");
        var options = TelegramChannelProviderFactory.ParseOptions(connection);
        Assert.False(options.RichMessages);
    }

    [Fact]
    public void ParseOptions_RichMessagesFalseBool_DisablesRichMessages()
    {
        var connection = ConnectionWithConfig("""{"botToken":"t","richMessages":false}""");
        var options = TelegramChannelProviderFactory.ParseOptions(connection);
        Assert.False(options.RichMessages);
    }

    [Fact]
    public void ParseOptions_RichMessagesAbsent_DefaultsToTrue()
    {
        var connection = ConnectionWithConfig("""{"botToken":"t"}""");
        var options = TelegramChannelProviderFactory.ParseOptions(connection);
        Assert.True(options.RichMessages);
    }
}
