using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using OpenAgent.Contracts;

namespace OpenAgent.Api.Endpoints;

/// <summary>
/// Conversation management — list, retrieve, and delete conversations.
/// Conversations are created implicitly on first interaction.
/// </summary>
public static class ConversationEndpoints
{
    /// <summary>
    /// Maps read/delete endpoints under /api/conversations.
    /// </summary>
    public static void MapConversationEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/conversations");

        group.MapGet("/{conversationId}", (string conversationId, IConversationStore store) =>
        {
            var conversation = store.Get(conversationId);
            return conversation is null
                ? Results.NotFound()
                : Results.Ok(new { conversation.Id });
        });

        group.MapDelete("/{conversationId}", (string conversationId, IConversationStore store) =>
        {
            store.Delete(conversationId);
            return Results.NoContent();
        });
    }
}
