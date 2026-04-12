using System.Diagnostics;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using OpenAgent.Contracts;
using OpenAgent.Models.Tools;

namespace OpenAgent.Api.Endpoints;

/// <summary>
/// Tool discovery and execution — list available tools and execute them directly
/// without going through an LLM provider.
/// </summary>
public static class ToolEndpoints
{
    /// <summary>
    /// Maps tool endpoints under /api/tools.
    /// </summary>
    public static void MapToolEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/tools").RequireAuthorization();

        // List all available tools with their definitions
        group.MapGet("/", (IAgentLogic agentLogic) =>
        {
            var tools = agentLogic.Tools.Select(t => new ToolDefinitionResponse
            {
                Name = t.Name,
                Description = t.Description,
                Parameters = t.Parameters
            });

            return Results.Ok(tools);
        });

        // Get a single tool definition by name
        group.MapGet("/{toolName}", (string toolName, IAgentLogic agentLogic) =>
        {
            var tool = agentLogic.Tools.FirstOrDefault(t => t.Name == toolName);
            if (tool is null)
                return Results.NotFound();

            return Results.Ok(new ToolDefinitionResponse
            {
                Name = tool.Name,
                Description = tool.Description,
                Parameters = tool.Parameters
            });
        });

        // Execute a tool directly — request body is the tool's arguments JSON
        group.MapPost("/{toolName}/execute", async (
            string toolName,
            JsonElement arguments,
            IAgentLogic agentLogic,
            CancellationToken ct) =>
        {
            var tool = agentLogic.Tools.FirstOrDefault(t => t.Name == toolName);
            if (tool is null)
                return Results.NotFound();

            // Use a throwaway conversation ID so tool execution doesn't pollute real conversations
            var conversationId = $"tool-test-{Guid.NewGuid():N}";

            var sw = Stopwatch.StartNew();
            var result = await agentLogic.ExecuteToolAsync(conversationId, toolName, arguments.GetRawText(), ct);
            sw.Stop();

            return Results.Ok(new ToolExecutionResponse
            {
                Tool = toolName,
                Result = result,
                DurationMs = sw.ElapsedMilliseconds
            });
        });
    }
}
