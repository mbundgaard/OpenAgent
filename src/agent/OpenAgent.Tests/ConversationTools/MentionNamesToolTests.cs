using System.Text.Json;
using OpenAgent.Models.Conversations;
using OpenAgent.Tests.Fakes;
using OpenAgent.Tools.Conversation;

namespace OpenAgent.Tests.ConversationTools;

public class MentionNamesToolTests
{
    private readonly InMemoryConversationStore _store = new();

    private void Seed(string conversationId) =>
        _store.GetOrCreate(conversationId, "app", ConversationType.Text, "p", "m");

    [Fact]
    public async Task Set_ReplacesListAndPersistsNormalization()
    {
        Seed("c1");
        var tool = new SetMentionNamesTool(_store);

        var result = await tool.ExecuteAsync(
            JsonSerializer.Serialize(new { names = new[] { "  Dex ", "fox", "  " } }), "c1");
        var doc = JsonDocument.Parse(result);

        Assert.Equal("set", doc.RootElement.GetProperty("status").GetString());
        var names = doc.RootElement.GetProperty("mention_names").EnumerateArray().Select(e => e.GetString()).ToArray();
        Assert.Equal(new[] { "Dex", "fox" }, names);

        Assert.Equal(new[] { "Dex", "fox" }, _store.Get("c1")!.MentionNames);
    }

    [Fact]
    public async Task Set_RejectsEmptyArray()
    {
        Seed("c1");
        var tool = new SetMentionNamesTool(_store);

        var result = await tool.ExecuteAsync(
            JsonSerializer.Serialize(new { names = Array.Empty<string>() }), "c1");
        var doc = JsonDocument.Parse(result);

        Assert.Contains("clear_mention_names", doc.RootElement.GetProperty("error").GetString());
        Assert.Null(_store.Get("c1")!.MentionNames);
    }

    [Fact]
    public async Task Set_RejectsAllWhitespaceEntries()
    {
        Seed("c1");
        var tool = new SetMentionNamesTool(_store);

        var result = await tool.ExecuteAsync(
            JsonSerializer.Serialize(new { names = new[] { "", "  ", "\t" } }), "c1");
        var doc = JsonDocument.Parse(result);

        Assert.Contains("At least one non-empty name", doc.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Set_RejectsOversizeName()
    {
        Seed("c1");
        var tool = new SetMentionNamesTool(_store);

        var longName = new string('x', 51);
        var result = await tool.ExecuteAsync(
            JsonSerializer.Serialize(new { names = new[] { longName } }), "c1");
        var doc = JsonDocument.Parse(result);

        Assert.Contains("max length", doc.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Set_MissingConversation_ReturnsError()
    {
        var tool = new SetMentionNamesTool(_store);

        var result = await tool.ExecuteAsync(
            JsonSerializer.Serialize(new { names = new[] { "Dex" } }), "does-not-exist");
        var doc = JsonDocument.Parse(result);

        Assert.Equal("Conversation not found", doc.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Clear_RemovesExistingList()
    {
        Seed("c1");
        var conversation = _store.Get("c1")!;
        conversation.MentionNames = ["Dex"];
        _store.Update(conversation);

        var tool = new ClearMentionNamesTool(_store);
        var result = await tool.ExecuteAsync("{}", "c1");
        var doc = JsonDocument.Parse(result);

        Assert.Equal("cleared", doc.RootElement.GetProperty("status").GetString());
        Assert.Null(_store.Get("c1")!.MentionNames);
    }

    [Fact]
    public async Task Clear_WhenUnset_ReturnsNotSet()
    {
        Seed("c1");

        var tool = new ClearMentionNamesTool(_store);
        var result = await tool.ExecuteAsync("{}", "c1");
        var doc = JsonDocument.Parse(result);

        Assert.Equal("not_set", doc.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Clear_MissingConversation_ReturnsError()
    {
        var tool = new ClearMentionNamesTool(_store);
        var result = await tool.ExecuteAsync("{}", "does-not-exist");
        var doc = JsonDocument.Parse(result);

        Assert.Equal("Conversation not found", doc.RootElement.GetProperty("error").GetString());
    }
}
