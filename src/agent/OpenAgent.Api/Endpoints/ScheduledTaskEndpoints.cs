using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using OpenAgent.ScheduledTasks;
using OpenAgent.ScheduledTasks.Models;
using System.Text.Json;

namespace OpenAgent.Api.Endpoints;

/// <summary>
/// REST endpoints for scheduled task CRUD and manual execution.
/// </summary>
public static class ScheduledTaskEndpoints
{
    /// <summary>
    /// Maps all scheduled task endpoints under /api/scheduled-tasks.
    /// </summary>
    public static void MapScheduledTaskEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/scheduled-tasks").RequireAuthorization();

        // GET / — list all
        group.MapGet("/", async (ScheduledTaskService service, CancellationToken ct) =>
        {
            var tasks = await service.ListAsync(ct);
            return Results.Ok(tasks);
        });

        // GET /{taskId} — get single
        group.MapGet("/{taskId}", async (string taskId, ScheduledTaskService service, CancellationToken ct) =>
        {
            var task = await service.GetAsync(taskId, ct);
            return task is not null ? Results.Ok(task) : Results.NotFound();
        });

        // POST / — create (client must provide id, or use a generated GUID)
        group.MapPost("/", async (ScheduledTask task, ScheduledTaskService service, CancellationToken ct) =>
        {
            try
            {
                await service.AddAsync(task, ct);
                return Results.Created($"/api/scheduled-tasks/{task.Id}", task);
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        // PUT /{taskId} — update
        group.MapPut("/{taskId}", async (string taskId, JsonElement body, ScheduledTaskService service, CancellationToken ct) =>
        {
            try
            {
                await service.UpdateAsync(taskId, task =>
                {
                    if (body.TryGetProperty("name", out var n)) task.Name = n.GetString()!;
                    if (body.TryGetProperty("prompt", out var p)) task.Prompt = p.GetString()!;
                    if (body.TryGetProperty("enabled", out var e)) task.Enabled = e.GetBoolean();
                    if (body.TryGetProperty("description", out var d)) task.Description = d.GetString();
                    if (body.TryGetProperty("schedule", out var s))
                        task.Schedule = JsonSerializer.Deserialize<ScheduleConfig>(s.GetRawText())!;
                    if (body.TryGetProperty("conversationId", out var cid))
                        task.ConversationId = cid.GetString();
                }, ct);

                // Re-fetch to return updated state
                var updated = await service.GetAsync(taskId, ct);
                return Results.Ok(updated);
            }
            catch (KeyNotFoundException) { return Results.NotFound(); }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        // DELETE /{taskId}
        group.MapDelete("/{taskId}", async (string taskId, ScheduledTaskService service, CancellationToken ct) =>
        {
            var existing = await service.GetAsync(taskId, ct);
            if (existing is null) return Results.NotFound();

            await service.RemoveAsync(taskId, ct);
            return Results.NoContent();
        });

        // POST /{taskId}/run — execute immediately
        group.MapPost("/{taskId}/run", async (string taskId, ScheduledTaskService service, CancellationToken ct) =>
        {
            try
            {
                await service.RunNowAsync(taskId, promptOverride: null, ct);
                return Results.Ok(new { status = "executed", taskId });
            }
            catch (KeyNotFoundException) { return Results.NotFound(); }
        });

        // POST /{taskId}/trigger — webhook trigger with optional body
        group.MapPost("/{taskId}/trigger", async (string taskId, HttpRequest request, ScheduledTaskService service, CancellationToken ct) =>
        {
            try
            {
                string? promptOverride = null;
                if (request.ContentLength > 0)
                {
                    var task = await service.GetAsync(taskId, ct);
                    if (task is null) return Results.NotFound();

                    using var reader = new StreamReader(request.Body);
                    var body = await reader.ReadToEndAsync(ct);
                    promptOverride = $"{task.Prompt}\n\n<webhook_context>\n{body}\n</webhook_context>";
                }

                await service.RunNowAsync(taskId, promptOverride, ct);
                return Results.Ok(new { status = "triggered", taskId });
            }
            catch (KeyNotFoundException) { return Results.NotFound(); }
        });
    }
}
