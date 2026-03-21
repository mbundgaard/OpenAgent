using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using OpenAgent.Contracts;
using OpenAgent.Models.Common;
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
            ILlmTextProvider textProvider,
            CancellationToken ct) =>
        {
            var conversation = store.GetOrCreate(conversationId, "app", ConversationType.Text, "azure-openai-text", "gpt-5.2-chat");

            var userMessage = new Message
            {
                Id = Guid.NewGuid().ToString(),
                ConversationId = conversationId,
                Role = "user",
                Content = request.Content
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
                    _ => new { type = "unknown" } as object
                });
            }

            return Results.Json(events, JsonOptions);
        }).RequireAuthorization();
    }
}
