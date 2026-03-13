using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using OpenAgent.Contracts;
using OpenAgent.Models.Connections;

namespace OpenAgent.Api.Endpoints;

/// <summary>
/// CRUD endpoints for managing channel connections.
/// </summary>
public static class ConnectionEndpoints
{
    /// <summary>
    /// Maps connection management endpoints under /api/connections.
    /// </summary>
    public static void MapConnectionEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/connections").RequireAuthorization();

        // List all connections with runtime status
        group.MapGet("/", (IConnectionStore store, IConnectionManager connectionManager) =>
        {
            var connections = store.LoadAll();
            return Results.Ok(connections.Select(c => ToResponse(c, connectionManager)));
        });

        // Get single connection
        group.MapGet("/{connectionId}", (string connectionId, IConnectionStore store, IConnectionManager connectionManager) =>
        {
            var connection = store.Load(connectionId);
            return connection is null
                ? Results.NotFound()
                : Results.Ok(ToResponse(connection, connectionManager));
        });

        // Create connection
        group.MapPost("/", async (CreateConnectionRequest request, IConnectionStore store, IConnectionManager connectionManager, CancellationToken ct) =>
        {
            var connectionId = Guid.NewGuid().ToString("N")[..12];

            var connection = new Connection
            {
                Id = connectionId,
                Name = request.Name,
                Type = request.Type,
                Enabled = request.Enabled,
                ConversationId = request.ConversationId ?? Guid.NewGuid().ToString(),
                Config = request.Config,
            };

            store.Save(connection);

            // Auto-start if enabled
            if (connection.Enabled)
            {
                try
                {
                    await connectionManager.StartConnectionAsync(connectionId, ct);
                }
                catch
                {
                    // Saved but failed to start — caller can check status
                }
            }

            return Results.Created($"/api/connections/{connectionId}", ToResponse(connection, connectionManager));
        });

        // Update connection
        group.MapPut("/{connectionId}", async (string connectionId, UpdateConnectionRequest request,
            IConnectionStore store, IConnectionManager connectionManager, CancellationToken ct) =>
        {
            var existing = store.Load(connectionId);
            if (existing is null)
                return Results.NotFound();

            // Stop if running before updating
            if (connectionManager.IsRunning(connectionId))
                await connectionManager.StopConnectionAsync(connectionId, ct);

            existing.Name = request.Name ?? existing.Name;
            existing.Type = request.Type ?? existing.Type;
            existing.Enabled = request.Enabled ?? existing.Enabled;
            existing.ConversationId = request.ConversationId ?? existing.ConversationId;
            if (request.Config.ValueKind != JsonValueKind.Undefined)
                existing.Config = request.Config;

            store.Save(existing);

            // Restart if enabled
            if (existing.Enabled)
            {
                try
                {
                    await connectionManager.StartConnectionAsync(connectionId, ct);
                }
                catch
                {
                    // Saved but failed to start
                }
            }

            return Results.Ok(ToResponse(existing, connectionManager));
        });

        // Delete connection
        group.MapDelete("/{connectionId}", async (string connectionId, IConnectionStore store, IConnectionManager connectionManager, CancellationToken ct) =>
        {
            if (connectionManager.IsRunning(connectionId))
                await connectionManager.StopConnectionAsync(connectionId, ct);

            store.Delete(connectionId);
            return Results.NoContent();
        });

        // Start connection
        group.MapPost("/{connectionId}/start", async (string connectionId, IConnectionStore store, IConnectionManager connectionManager, CancellationToken ct) =>
        {
            var connection = store.Load(connectionId);
            if (connection is null)
                return Results.NotFound();

            await connectionManager.StartConnectionAsync(connectionId, ct);
            return Results.Ok(ToResponse(connection, connectionManager));
        });

        // Stop connection
        group.MapPost("/{connectionId}/stop", async (string connectionId, IConnectionStore store, IConnectionManager connectionManager, CancellationToken ct) =>
        {
            var connection = store.Load(connectionId);
            if (connection is null)
                return Results.NotFound();

            await connectionManager.StopConnectionAsync(connectionId, ct);
            return Results.Ok(ToResponse(connection, connectionManager));
        });
    }

    private static ConnectionResponse ToResponse(Connection connection, IConnectionManager connectionManager) => new()
    {
        Id = connection.Id,
        Name = connection.Name,
        Type = connection.Type,
        Enabled = connection.Enabled,
        ConversationId = connection.ConversationId,
        Config = connection.Config,
        Status = connectionManager.IsRunning(connection.Id) ? "running" : "stopped",
    };
}

/// <summary>Connection response with runtime status.</summary>
public sealed class ConnectionResponse
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("type")]
    public required string Type { get; init; }

    [JsonPropertyName("enabled")]
    public bool Enabled { get; init; }

    [JsonPropertyName("conversationId")]
    public required string ConversationId { get; init; }

    [JsonPropertyName("config")]
    public JsonElement Config { get; init; }

    [JsonPropertyName("status")]
    public required string Status { get; init; }
}

/// <summary>Request body for creating a connection.</summary>
public sealed class CreateConnectionRequest
{
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("type")]
    public required string Type { get; init; }

    [JsonPropertyName("enabled")]
    public bool Enabled { get; init; }

    [JsonPropertyName("conversationId")]
    public string? ConversationId { get; init; }

    [JsonPropertyName("config")]
    public JsonElement Config { get; init; }
}

/// <summary>Request body for updating a connection.</summary>
public sealed class UpdateConnectionRequest
{
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("type")]
    public string? Type { get; init; }

    [JsonPropertyName("enabled")]
    public bool? Enabled { get; init; }

    [JsonPropertyName("conversationId")]
    public string? ConversationId { get; init; }

    [JsonPropertyName("config")]
    public JsonElement Config { get; init; }
}
