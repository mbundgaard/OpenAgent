using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using OpenAgent.Contracts;

namespace OpenAgent.Api.Chat;

public static class ChatEndpoints
{
    public static void MapChatEndpoints(this WebApplication app)
    {
        app.MapPost("/api/conversations/{id}/messages", async (
            string id,
            ChatRequest request,
            IConversationStore store,
            ILlmTextProvider textProvider,
            CancellationToken ct) =>
        {
            if (store.Get(id) is null)
                return Results.NotFound();

            var response = await textProvider.CompleteAsync(id, request.Content, ct);

            return Results.Ok(new { response.Role, response.Content });
        });
    }
}

public sealed record ChatRequest(string Content);
