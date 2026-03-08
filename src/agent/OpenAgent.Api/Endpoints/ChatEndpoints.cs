using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using OpenAgent.Contracts;
using OpenAgent.Models.Conversations;
using OpenAgent.Models.Text;

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
            var conversation = store.GetOrCreate(conversationId, "app", ConversationType.Text);

            var response = await textProvider.CompleteAsync(conversation, request.Content, ct);

            return Results.Ok(new ChatResponse
            {
                ConversationId = conversationId,
                Role = response.Role,
                Content = response.Content
            });
        });
    }
}
