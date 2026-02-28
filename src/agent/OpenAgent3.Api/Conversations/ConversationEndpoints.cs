namespace OpenAgent3.Api.Conversations;

public static class ConversationEndpoints
{
    public static void MapConversationEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/conversations");

        group.MapPost("/", (ConversationStore store) =>
        {
            var conversation = store.Create();
            return Results.Ok(new { conversation.Id });
        });

        group.MapGet("/{id}", (string id, ConversationStore store) =>
        {
            var conversation = store.Get(id);
            return conversation is null
                ? Results.NotFound()
                : Results.Ok(new { conversation.Id });
        });

        group.MapDelete("/{id}", (string id, ConversationStore store) =>
        {
            store.Delete(id);
            return Results.NoContent();
        });
    }
}
