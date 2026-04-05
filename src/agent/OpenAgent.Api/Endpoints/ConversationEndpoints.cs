using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using OpenAgent.Contracts;
using OpenAgent.Models.Conversations;

namespace OpenAgent.Api.Endpoints;

/// <summary>
/// Conversation management — list, retrieve, update, and delete conversations.
/// Conversations are created implicitly on first interaction.
/// </summary>
public static class ConversationEndpoints
{
    /// <summary>
    /// Maps read/update/delete endpoints under /api/conversations.
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
                CreatedAt = c.CreatedAt,
                TotalPromptTokens = c.TotalPromptTokens,
                TotalCompletionTokens = c.TotalCompletionTokens,
                TurnCount = c.TurnCount,
                LastActivity = c.LastActivity,
                ActiveSkills = c.ActiveSkills
            }));
        });

        group.MapGet("/{conversationId}", (string conversationId, IConversationStore store) =>
        {
            var conversation = store.Get(conversationId);
            if (conversation is null)
                return Results.NotFound();

            return Results.Ok(new ConversationDetailResponse
            {
                Id = conversation.Id,
                Source = conversation.Source,
                Type = conversation.Type,
                Provider = conversation.Provider,
                Model = conversation.Model,
                CreatedAt = conversation.CreatedAt,
                TotalPromptTokens = conversation.TotalPromptTokens,
                TotalCompletionTokens = conversation.TotalCompletionTokens,
                TurnCount = conversation.TurnCount,
                LastActivity = conversation.LastActivity,
                VoiceSessionId = conversation.VoiceSessionId,
                VoiceSessionOpen = conversation.VoiceSessionOpen,
                CompactionRunning = conversation.CompactionRunning,
                ActiveSkills = conversation.ActiveSkills
            });
        });

        group.MapGet("/{conversationId}/messages", (string conversationId, IConversationStore store) =>
        {
            var conversation = store.Get(conversationId);
            if (conversation is null)
                return Results.NotFound();

            var messages = store.GetMessages(conversationId);
            return Results.Ok(messages);
        });

        group.MapPatch("/{conversationId}", (string conversationId, UpdateConversationRequest request, IConversationStore store) =>
        {
            var conversation = store.Get(conversationId);
            if (conversation is null)
                return Results.NotFound();

            if (request.Source is not null)
                conversation.Source = request.Source;
            if (request.Provider is not null)
                conversation.Provider = request.Provider;
            if (request.Model is not null)
                conversation.Model = request.Model;

            store.Update(conversation);

            return Results.Ok(new ConversationDetailResponse
            {
                Id = conversation.Id,
                Source = conversation.Source,
                Type = conversation.Type,
                Provider = conversation.Provider,
                Model = conversation.Model,
                CreatedAt = conversation.CreatedAt,
                TotalPromptTokens = conversation.TotalPromptTokens,
                TotalCompletionTokens = conversation.TotalCompletionTokens,
                TurnCount = conversation.TurnCount,
                LastActivity = conversation.LastActivity,
                VoiceSessionId = conversation.VoiceSessionId,
                VoiceSessionOpen = conversation.VoiceSessionOpen,
                CompactionRunning = conversation.CompactionRunning,
                ActiveSkills = conversation.ActiveSkills
            });
        });

        group.MapDelete("/{conversationId}", (string conversationId, IConversationStore store) =>
        {
            store.Delete(conversationId);
            return Results.NoContent();
        });
    }
}
