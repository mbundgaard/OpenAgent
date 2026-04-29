using OpenAgent.App.Core.Models;

namespace OpenAgent.App.Core.Api;

/// <summary>HTTP client surface to the agent's REST endpoints. Implementations read credentials per call so token rotation takes effect immediately.</summary>
public interface IApiClient
{
    /// <summary>List all conversations (GET /api/conversations).</summary>
    Task<List<ConversationListItem>> GetConversationsAsync(CancellationToken ct = default);

    /// <summary>Delete a conversation (DELETE /api/conversations/{conversationId}).</summary>
    Task DeleteConversationAsync(string conversationId, CancellationToken ct = default);

    /// <summary>Rename a conversation by setting its intention (PATCH /api/conversations/{conversationId}).</summary>
    Task RenameConversationAsync(string conversationId, string intention, CancellationToken ct = default);
}
