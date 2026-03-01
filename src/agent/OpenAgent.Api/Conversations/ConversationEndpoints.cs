using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using OpenAgent.Contracts;

namespace OpenAgent.Api.Conversations;

public static class ConversationEndpoints
{
    public static void MapConversationEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/conversations");

        group.MapPost("/", (IConversationStore store) =>
        {
            var conversation = store.Create();
            return Results.Ok(new { conversation.Id });
        });

        group.MapGet("/{id}", (string id, IConversationStore store) =>
        {
            var conversation = store.Get(id);
            return conversation is null
                ? Results.NotFound()
                : Results.Ok(new { conversation.Id });
        });

        group.MapDelete("/{id}", (string id, IConversationStore store) =>
        {
            store.Delete(id);
            return Results.NoContent();
        });
    }
}
