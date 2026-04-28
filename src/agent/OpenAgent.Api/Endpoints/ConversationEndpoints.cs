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
            var conversation = store.GetOrCreate(
                conversationId,
                "app",
                agentConfig.TextProvider, agentConfig.TextModel,
                agentConfig.VoiceProvider, agentConfig.VoiceModel);
            return Results.Ok(new { id = conversation.Id });
        });

        group.MapGet("/", (IConversationStore store) =>
        {
            var conversations = store.GetAll();
            return Results.Ok(conversations.Select(c => new ConversationListItemResponse
            {
                Id = c.Id,
                Source = c.Source,
                TextProvider = c.TextProvider,
                TextModel = c.TextModel,
                VoiceProvider = c.VoiceProvider,
                VoiceModel = c.VoiceModel,
                CreatedAt = c.CreatedAt,
                TotalPromptTokens = c.TotalPromptTokens,
                TotalCompletionTokens = c.TotalCompletionTokens,
                TurnCount = c.TurnCount,
                LastActivity = c.LastActivity,
                ActiveSkills = c.ActiveSkills,
                DisplayName = c.DisplayName,
                Intention = c.Intention,
                MentionFilter = c.MentionFilter
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
                TextProvider = conversation.TextProvider,
                TextModel = conversation.TextModel,
                VoiceProvider = conversation.VoiceProvider,
                VoiceModel = conversation.VoiceModel,
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
                Intention = conversation.Intention,
                MentionFilter = conversation.MentionFilter
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
            if (request.TextProvider is not null)
                conversation.TextProvider = request.TextProvider;
            if (request.TextModel is not null)
                conversation.TextModel = request.TextModel;
            if (request.VoiceProvider is not null)
                conversation.VoiceProvider = request.VoiceProvider;
            if (request.VoiceModel is not null)
                conversation.VoiceModel = request.VoiceModel;
            if (request.ChannelChatId is not null)
                conversation.ChannelChatId = request.ChannelChatId;
            // Empty string explicitly clears the intention; null leaves it unchanged.
            if (request.Intention is not null)
                conversation.Intention = request.Intention.Length == 0 ? null : request.Intention;
            // Empty list explicitly clears the mention filter; null leaves it unchanged.
            if (request.MentionFilter is not null)
                conversation.MentionFilter = request.MentionFilter.Count == 0 ? null : request.MentionFilter;

            store.Update(conversation);

            return Results.Ok(new ConversationDetailResponse
            {
                Id = conversation.Id,
                Source = conversation.Source,
                TextProvider = conversation.TextProvider,
                TextModel = conversation.TextModel,
                VoiceProvider = conversation.VoiceProvider,
                VoiceModel = conversation.VoiceModel,
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
                Intention = conversation.Intention,
                MentionFilter = conversation.MentionFilter
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
