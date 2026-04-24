using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using OpenAgent.Contracts;
using OpenAgent.Models.Configs;
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

        group.MapPost("/", (IConversationStore store, AgentConfig agentConfig) =>
        {
            var conversationId = Guid.NewGuid().ToString();
            var conversation = store.GetOrCreate(conversationId, "app", ConversationType.Text, agentConfig.TextProvider, agentConfig.TextModel);
            return Results.Ok(new { id = conversation.Id });
        });

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
                ActiveSkills = c.ActiveSkills,
                DisplayName = c.DisplayName,
                Intention = c.Intention
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
                ActiveSkills = conversation.ActiveSkills,
                ChannelType = conversation.ChannelType,
                ConnectionId = conversation.ConnectionId,
                ChannelChatId = conversation.ChannelChatId,
                DisplayName = conversation.DisplayName,
                Intention = conversation.Intention
            });
        });

        group.MapGet("/{conversationId}/messages", (string conversationId, int? tail, IConversationStore store) =>
        {
            var conversation = store.Get(conversationId);
            if (conversation is null)
                return Results.NotFound();

            var messages = store.GetMessages(conversationId);
            // tail=N returns only the last N messages (for initial UI load)
            if (tail is > 0)
                messages = messages.TakeLast(tail.Value).ToList();
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
            if (request.ChannelChatId is not null)
                conversation.ChannelChatId = request.ChannelChatId;
            // Empty string explicitly clears the intention; null leaves it unchanged.
            if (request.Intention is not null)
                conversation.Intention = request.Intention.Length == 0 ? null : request.Intention;

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
                ActiveSkills = conversation.ActiveSkills,
                ChannelType = conversation.ChannelType,
                ConnectionId = conversation.ConnectionId,
                ChannelChatId = conversation.ChannelChatId,
                DisplayName = conversation.DisplayName,
                Intention = conversation.Intention
            });
        });

        group.MapDelete("/{conversationId}", (string conversationId, IConversationStore store) =>
        {
            store.Delete(conversationId);
            return Results.NoContent();
        });

        group.MapPost("/{conversationId}/compact", async (
            string conversationId,
            CompactRequest? body,
            IConversationStore store,
            CancellationToken ct) =>
        {
            if (store.Get(conversationId) is null)
                return Results.NotFound();

            var compacted = await store.CompactNowAsync(
                conversationId,
                CompactionReason.Manual,
                body?.Instructions,
                ct);

            return Results.Ok(new { compacted });
        });
    }
}

/// <summary>Optional body for POST /api/conversations/{id}/compact.</summary>
public sealed record CompactRequest(
    [property: System.Text.Json.Serialization.JsonPropertyName("instructions")] string? Instructions);
