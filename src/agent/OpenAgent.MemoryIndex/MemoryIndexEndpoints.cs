using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace OpenAgent.MemoryIndex;

/// <summary>
/// REST endpoints for the memory index — manual trigger and stats. Both routes sit
/// behind the same authorization as the rest of the API.
/// </summary>
public static class MemoryIndexEndpoints
{
    public static void MapMemoryIndexEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/memory-index").RequireAuthorization();

        group.MapPost("/run", async (MemoryIndexService service, CancellationToken ct) =>
        {
            var result = await service.RunAsync(ct);
            return Results.Ok(result);
        });

        group.MapGet("/stats", (MemoryIndexService service) =>
        {
            return Results.Ok(service.GetStats());
        });
    }
}
