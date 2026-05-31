using System.Net.WebSockets;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using OpenAgent.BackgroundAgent;
using OpenAgent.Contracts;
using OpenAgent.Models.Configs;
using OpenAgent.ScheduledTasks;
using OpenAgent.Tests.Fakes;

namespace OpenAgent.Tests;

public class PostToMainToolTests
{
    private const string MainId = "main-conv-id";
    private const string BgId = "bg-conv-id";

    private static (PostToMainTool tool, InMemoryConversationStore store, AgentConfig config) BuildTool(string? mainConversationId = MainId)
    {
        var store = new InMemoryConversationStore();
        if (mainConversationId is not null)
            store.GetOrCreate(mainConversationId, "telegram", "p", "m", "vp", "vm");

        var config = new AgentConfig { MainConversationId = mainConversationId };
        var router = new DeliveryRouter(
            new NoopConnectionManager(),
            new NoopWebSocketRegistry(),
            NullLogger<DeliveryRouter>.Instance);

        var tool = new PostToMainTool(store, router, config, NullLogger<PostToMainTool>.Instance);
        return (tool, store, config);
    }

    [Fact]
    public async Task Posts_prefixed_message_to_main_conversation()
    {
        var (tool, store, _) = BuildTool();
        var args = JsonSerializer.Serialize(new { message = "sqlite-vec has a 4096-dim limit. relevant to the memory design." });

        var result = await tool.ExecuteAsync(args, BgId);

        var parsed = JsonDocument.Parse(result).RootElement;
        Assert.Equal("posted", parsed.GetProperty("status").GetString());

        var messages = store.GetMessages(MainId);
        var posted = Assert.Single(messages);
        Assert.Equal("assistant", posted.Role);
        Assert.StartsWith("[Background] ", posted.Content);
        Assert.Contains("4096-dim", posted.Content);
    }

    [Fact]
    public async Task Returns_error_when_main_conversation_id_unset()
    {
        var (tool, _, _) = BuildTool(mainConversationId: null);
        var args = JsonSerializer.Serialize(new { message = "hi" });

        var result = await tool.ExecuteAsync(args, BgId);

        Assert.Contains("MainConversationId", JsonDocument.Parse(result).RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Returns_error_when_main_conversation_does_not_exist()
    {
        var (tool, store, _) = BuildTool();
        store.Delete(MainId);
        var args = JsonSerializer.Serialize(new { message = "hi" });

        var result = await tool.ExecuteAsync(args, BgId);

        Assert.Contains("not found", JsonDocument.Parse(result).RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Refuses_to_post_to_calling_conversation()
    {
        var (tool, _, _) = BuildTool();
        var args = JsonSerializer.Serialize(new { message = "hi" });

        var result = await tool.ExecuteAsync(args, MainId); // calling from main, targeting main

        Assert.Contains("calling conversation", JsonDocument.Parse(result).RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Treats_empty_sentinel_as_no_op()
    {
        var (tool, store, _) = BuildTool();
        var args = JsonSerializer.Serialize(new { message = "[]" });

        var result = await tool.ExecuteAsync(args, BgId);

        Assert.Equal("skipped", JsonDocument.Parse(result).RootElement.GetProperty("status").GetString());
        Assert.Empty(store.GetMessages(MainId));
    }

    [Fact]
    public async Task Rejects_empty_or_whitespace_message()
    {
        var (tool, _, _) = BuildTool();
        var args = JsonSerializer.Serialize(new { message = "   " });

        var result = await tool.ExecuteAsync(args, BgId);

        Assert.Contains("empty", JsonDocument.Parse(result).RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Rejects_message_over_length_cap()
    {
        var (tool, _, _) = BuildTool();
        var args = JsonSerializer.Serialize(new { message = new string('x', 5000) });

        var result = await tool.ExecuteAsync(args, BgId);

        Assert.Contains("too long", JsonDocument.Parse(result).RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Invalid_json_arguments_returns_error()
    {
        var (tool, _, _) = BuildTool();

        var result = await tool.ExecuteAsync("not json", BgId);

        Assert.Contains("Invalid arguments JSON", JsonDocument.Parse(result).RootElement.GetProperty("error").GetString());
    }

    private sealed class NoopConnectionManager : IConnectionManager
    {
        public bool IsRunning(string connectionId) => false;
        public IChannelProvider? GetProvider(string connectionId) => null;
        public Task StartConnectionAsync(string connectionId, CancellationToken ct) => Task.CompletedTask;
        public Task StopConnectionAsync(string connectionId, CancellationToken ct) => Task.CompletedTask;
        public IEnumerable<(string ConnectionId, IChannelProvider Provider)> GetProviders() => [];
    }

    private sealed class NoopWebSocketRegistry : IWebSocketRegistry
    {
        public void Register(string conversationId, WebSocket webSocket) { }
        public void Unregister(string conversationId, WebSocket webSocket) { }
        public WebSocket? Get(string conversationId) => null;
    }
}
