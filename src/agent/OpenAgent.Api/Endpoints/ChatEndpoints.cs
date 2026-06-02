using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using OpenAgent.Contracts;
using OpenAgent.Models.Common;
using OpenAgent.Models.Configs;
using OpenAgent.Models.Conversations;
using OpenAgent.Models.Text;

namespace OpenAgent.Api.Endpoints;

/// <summary>
/// Text completion over REST — send a message, receive all completion events as a JSON array.
/// </summary>
public static class ChatEndpoints
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>
    /// Maps POST /api/conversations/{conversationId}/messages for request/response text interaction.
    /// Returns all completion events (text deltas, tool calls, tool results) as a JSON array.
    /// </summary>
    public static void MapChatEndpoints(this WebApplication app)
    {
        app.MapPost("/api/conversations/{conversationId}/messages", async (
            string conversationId,
            HttpRequest httpRequest,
            IConversationStore store,
            AgentConfig agentConfig,
            IServiceProvider services,
            CancellationToken ct) =>
        {
            // Parse the body explicitly so a malformed request (most commonly a non-ASCII
            // character sent as a legacy code page instead of UTF-8 — e.g. a Windows shell
            // passing an em-dash via `curl -d`) returns an actionable message instead of the
            // empty 400 body the framework emits in Production.
            ChatRequest? request;
            try
            {
                request = await httpRequest.ReadFromJsonAsync<ChatRequest>(ct);
            }
            catch (Exception ex) when (ex is JsonException or BadHttpRequestException)
            {
                return Results.Problem(
                    detail: "Request body must be UTF-8 encoded JSON, e.g. {\"content\":\"...\"}. " +
                            "Send non-ASCII characters as UTF-8 (escape them as \\uXXXX or post raw " +
                            "UTF-8 bytes), not a legacy code page such as Windows-1252.",
                    statusCode: StatusCodes.Status400BadRequest);
            }

            var conversation = store.GetOrCreate(conversationId, "app",
                agentConfig.TextProvider, agentConfig.TextModel,
                agentConfig.VoiceProvider, agentConfig.VoiceModel);

            // Drop messages that don't mention any required name
            if (!MentionMatcher.ShouldAccept(conversation, request.Content ?? string.Empty))
                return Results.Json(Array.Empty<object>(), JsonOptions);

            var textProvider = services.GetRequiredKeyedService<ILlmTextProvider>(conversation.TextProvider);

            var userMessage = new Message
            {
                Id = Guid.NewGuid().ToString(),
                ConversationId = conversationId,
                Role = "user",
                Content = request.Content,
                Modality = MessageModality.Text
            };

            // Collect all completion events
            var events = new List<object>();
            await foreach (var evt in textProvider.CompleteAsync(conversation, userMessage, ct))
            {
                // AssistantMessageSaved is an internal channel-association signal (carries the
                // persisted message ID for channel providers), not app-facing output. The WebSocket
                // endpoint already skips it; mirror that here so it never surfaces as "unknown".
                if (evt is AssistantMessageSaved)
                    continue;

                events.Add(evt switch
                {
                    TextDelta delta => new { type = "text", delta.Content },
                    ToolCallEvent toolCall => new { type = "tool_call", toolCall.ToolCallId, toolCall.Name, toolCall.Arguments },
                    ToolResultEvent toolResult => new { type = "tool_result", toolResult.ToolCallId, toolResult.Name, toolResult.Result },
                    ThinkingStarted => new { type = "thinking_started" },
                    ThinkingStopped => new { type = "thinking_stopped" },
                    ResponseSuppressed => new { type = "response_suppressed" },
                    _ => new { type = "unknown" } as object
                });
            }

            return Results.Json(events, JsonOptions);
        }).RequireAuthorization();
    }
}
