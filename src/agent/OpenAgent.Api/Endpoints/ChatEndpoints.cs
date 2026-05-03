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
            ChatRequest request,
            IConversationStore store,
            AgentConfig agentConfig,
            IServiceProvider services,
            CancellationToken ct) =>
        {
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
                events.Add(evt switch
                {
                    TextDelta delta => new { type = "text", delta.Content },
                    ToolCallEvent toolCall => new { type = "tool_call", toolCall.ToolCallId, toolCall.Name, toolCall.Arguments },
                    ToolResultEvent toolResult => new { type = "tool_result", toolResult.ToolCallId, toolResult.Name, toolResult.Result },
                    ToolCallStarted => new { type = "tool_call_started" },
                    ToolCallCompleted => new { type = "tool_call_completed" },
                    _ => new { type = "unknown" } as object
                });
            }

            return Results.Json(events, JsonOptions);
        }).RequireAuthorization();
    }
}
