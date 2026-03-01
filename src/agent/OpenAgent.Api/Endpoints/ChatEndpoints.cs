using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using OpenAgent.Contracts;

namespace OpenAgent.Api.Endpoints;

/// <summary>
/// Synchronous text completion over REST — send a message, receive the full response.
/// </summary>
public static class ChatEndpoints
{
    /// <summary>
    /// Maps POST /api/conversations/{conversationId}/messages for request/response text interaction.
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
            if (store.Get(conversationId) is null)
                return Results.NotFound();

            var response = await textProvider.CompleteAsync(conversationId, request.Content, ct);

            return Results.Ok(new { response.Role, response.Content });
        });
    }
}

public sealed record ChatRequest(string Content);
