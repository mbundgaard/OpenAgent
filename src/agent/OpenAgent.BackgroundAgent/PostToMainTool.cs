using System.Text.Json;
using Microsoft.Extensions.Logging;
using OpenAgent.Contracts;
using OpenAgent.Models.Configs;
using OpenAgent.Models.Conversations;
using OpenAgent.ScheduledTasks;

namespace OpenAgent.BackgroundAgent;

/// <summary>
/// The single outbound capability the background agent has. Posts an assistant message into the
/// user's main conversation, prefixed with <c>[Background]</c>, and routes it through the existing
/// <see cref="DeliveryRouter"/> so it surfaces on whatever channel the main conversation is bound
/// to (Telegram, WhatsApp, app, web).
///
/// The bar for calling this tool is high — see BACKGROUND.md. The bg agent should prefer silence
/// (end its turn with <c>[]</c>) when uncertain. This tool is the only path from the bg
/// conversation to the user; keeping it as a deliberate, named action makes the discipline
/// mechanically obvious.
/// </summary>
public sealed class PostToMainTool : ITool
{
    private const string BackgroundPrefix = "[Background] ";
    private const int MaxMessageLength = 4000;

    private readonly IConversationStore _store;
    private readonly DeliveryRouter _deliveryRouter;
    private readonly AgentConfig _agentConfig;
    private readonly ILogger<PostToMainTool> _logger;

    public PostToMainTool(
        IConversationStore store,
        DeliveryRouter deliveryRouter,
        AgentConfig agentConfig,
        ILogger<PostToMainTool> logger)
    {
        _store = store;
        _deliveryRouter = deliveryRouter;
        _agentConfig = agentConfig;
        _logger = logger;
    }

    public AgentToolDefinition Definition { get; } = new()
    {
        Name = "post_to_main",
        Description = "Post a short message to the user's main conversation, prefixed with [Background]. " +
                      "This is the ONLY way the background agent reaches the user — use sparingly. " +
                      "Only call when you have something genuinely worth surfacing: a meaningful insight, " +
                      "a significant inbox find, or a connection between two things that feels new. " +
                      "If unsure, do not call this tool — end your turn with [] instead.",
        Parameters = new
        {
            type = "object",
            properties = new
            {
                message = new
                {
                    type = "string",
                    description = "The message body. Short, direct, no preamble. One or two sentences plus a link or reference. The [Background] prefix is added automatically."
                }
            },
            required = new[] { "message" }
        }
    };

    public async Task<string> ExecuteAsync(string arguments, string conversationId, CancellationToken ct = default)
    {
        string? rawMessage;
        try
        {
            var args = JsonDocument.Parse(arguments).RootElement;
            rawMessage = args.TryGetProperty("message", out var m) ? m.GetString() : null;
        }
        catch (JsonException ex)
        {
            return Error($"Invalid arguments JSON: {ex.Message}");
        }

        var message = rawMessage?.Trim();
        if (string.IsNullOrWhiteSpace(message))
            return Error("message is required and cannot be empty.");

        // Defense-in-depth: if the agent passed literally "[]" the turn would also be suppressed by
        // the provider, but we don't want to spam the main conv either. Treat as a no-op.
        if (message == "[]")
        {
            _logger.LogInformation("post_to_main called with '[]' sentinel — no-op");
            return JsonSerializer.Serialize(new { status = "skipped", reason = "empty sentinel" });
        }

        if (message.Length > MaxMessageLength)
            return Error($"message too long ({message.Length} chars, max {MaxMessageLength}).");

        var mainConversationId = _agentConfig.MainConversationId;
        if (string.IsNullOrWhiteSpace(mainConversationId))
            return Error("AgentConfig.MainConversationId is not set; cannot post to main conversation.");

        // Don't let a misconfigured bg conversation post to itself — that would just echo into the
        // bg agent's own history and confuse the next run.
        if (string.Equals(conversationId, mainConversationId, StringComparison.Ordinal))
            return Error("post_to_main cannot target the calling conversation.");

        var mainConversation = _store.Get(mainConversationId);
        if (mainConversation is null)
            return Error($"main conversation '{mainConversationId}' not found.");

        var prefixed = BackgroundPrefix + message;

        // Persist the message in the main conversation so it appears in history alongside
        // the rest of the chat — same shape as a scheduled task delivery.
        var assistantMessage = new Message
        {
            Id = Guid.NewGuid().ToString(),
            ConversationId = mainConversationId,
            Role = "assistant",
            Content = prefixed
        };
        _store.AddMessage(mainConversationId, assistantMessage);

        try
        {
            await _deliveryRouter.DeliverAsync(mainConversation, prefixed, ct);
        }
        catch (Exception ex)
        {
            // Delivery failures shouldn't fail the tool call — the message is in history and
            // the agent's run state stays clean. Log and report.
            _logger.LogError(ex, "post_to_main: delivery to main conversation '{Id}' failed", mainConversationId);
            return JsonSerializer.Serialize(new
            {
                status = "persisted_but_delivery_failed",
                error = ex.Message,
                main_conversation_id = mainConversationId
            });
        }

        _logger.LogInformation("post_to_main: delivered {Length}-char message to main conversation '{Id}'",
            prefixed.Length, mainConversationId);

        return JsonSerializer.Serialize(new
        {
            status = "posted",
            main_conversation_id = mainConversationId,
            length = prefixed.Length
        });
    }

    private static string Error(string message) =>
        JsonSerializer.Serialize(new { error = message });
}
