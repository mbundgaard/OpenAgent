using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using OpenAgent.Contracts;
using OpenAgent.Models.Conversations;

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
        var group = app.MapGroup("/api/conversations").RequireAuthorization();

        group.MapGet("/", (IConversationStore store) =>
        {
            var conversations = store.GetAll();
            return Results.Ok(conversations.Select(c => new ConversationListItemResponse
            {
                Id = c.Id,
                Source = c.Source,
                Type = c.Type,
                Provider = c.Provider,
                Model = c.Model,
                CreatedAt = c.CreatedAt
            }));
        });

        group.MapGet("/{conversationId}", (string conversationId, IConversationStore store) =>
        {
            var conversation = store.Get(conversationId);
            return conversation is null
                ? Results.NotFound()
                : Results.Ok(new ConversationIdResponse { Id = conversation.Id });
        });

        group.MapGet("/{conversationId}/messages", (string conversationId, IConversationStore store) =>
        {
            var conversation = store.Get(conversationId);
            if (conversation is null)
                return Results.NotFound();

            var messages = store.GetMessages(conversationId);
            return Results.Ok(messages);
        });

        group.MapDelete("/{conversationId}", (string conversationId, IConversationStore store) =>
        {
            store.Delete(conversationId);
            return Results.NoContent();
        });
    }
}
