using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using OpenAgent.Models.Configs;
using OpenAgent.ScheduledTasks.SystemJobs;

namespace OpenAgent.BackgroundAgent;

/// <summary>
/// REST endpoints for the background agent — visibility into its scheduling state and a
/// manual trigger that bypasses the three-gate check. Both routes sit behind the standard
/// API key authorization.
/// </summary>
public static class BackgroundAgentEndpoints
{
    public static void MapBackgroundAgentEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/background-agent").RequireAuthorization();

        // GET /api/background-agent/state — current scheduling + gate snapshot.
        group.MapGet("/state", async (
            BackgroundAgentRunner runner,
            SystemJobStateStore jobStateStore,
            AgentConfig agentConfig) =>
        {
            jobStateStore.Load();
            var state = jobStateStore.GetOrCreate(BackgroundAgentRunner.JobName);
            var now = DateTimeOffset.UtcNow;
            var eligible = await runner.ShouldRunAsync(now);

            return Results.Ok(new BackgroundAgentStateResponse(
                Name: BackgroundAgentRunner.JobName,
                ConversationId: agentConfig.MainConversationId ?? "",
                LastRunAt: state.LastRunAt,
                NextRunAt: state.NextRunAt,
                LastStatus: state.LastStatus,
                LastError: state.LastError,
                ConsecutiveErrors: state.ConsecutiveErrors,
                EligibleNow: eligible,
                CheckedAt: now));
        });

        // POST /api/background-agent/run — manual trigger. Bypasses ShouldRunAsync so it can be
        // used for debugging proactivity tuning without waiting for the 2h gate.
        group.MapPost("/run", async (BackgroundAgentRunner runner, CancellationToken ct) =>
        {
            var startedAt = DateTimeOffset.UtcNow;
            await runner.RunAsync(ct);
            return Results.Ok(new BackgroundAgentManualRunResponse(
                StartedAt: startedAt,
                DurationMs: (DateTimeOffset.UtcNow - startedAt).TotalMilliseconds));
        });
    }
}

/// <summary>Wire shape for <c>GET /api/background-agent/state</c>.</summary>
public sealed record BackgroundAgentStateResponse(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("conversationId")] string ConversationId,
    [property: JsonPropertyName("lastRunAt")] DateTimeOffset? LastRunAt,
    [property: JsonPropertyName("nextRunAt")] DateTimeOffset? NextRunAt,
    [property: JsonPropertyName("lastStatus")] string? LastStatus,
    [property: JsonPropertyName("lastError")] string? LastError,
    [property: JsonPropertyName("consecutiveErrors")] int ConsecutiveErrors,
    [property: JsonPropertyName("eligibleNow")] bool EligibleNow,
    [property: JsonPropertyName("checkedAt")] DateTimeOffset CheckedAt);

/// <summary>Wire shape for <c>POST /api/background-agent/run</c>.</summary>
public sealed record BackgroundAgentManualRunResponse(
    [property: JsonPropertyName("startedAt")] DateTimeOffset StartedAt,
    [property: JsonPropertyName("durationMs")] double DurationMs);
