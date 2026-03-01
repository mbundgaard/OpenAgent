using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using OpenAgent.Contracts;
using OpenAgent.Models.Conversations;

namespace OpenAgent.Api.Endpoints;

/// <summary>
/// Synchronous text completion over REST — send a message, receive the full response.
/// </summary>
public static class ChatEndpoints
{
    /// <summary>
    /// Maps POST /api/conversations/{conversationId}/messages for request/response text interaction.
    /// Creates the conversation automatically if it doesn't exist.
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
            store.GetOrCreate(conversationId, "app", ConversationType.Text);

            var response = await textProvider.CompleteAsync(conversationId, request.Content, ct);

            return Results.Ok(new { conversationId, response.Role, response.Content });
        });
    }
}

public sealed record ChatRequest(string Content);
