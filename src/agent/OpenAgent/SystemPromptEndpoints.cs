using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace OpenAgent;

/// <summary>
/// Admin endpoints for reading and editing the core system prompt markdown files.
/// </summary>
internal static class SystemPromptEndpoints
{
    private static readonly Dictionary<string, string> KeyToFile = new()
    {
        ["agents"] = "AGENTS.md",
        ["soul"] = "SOUL.md",
        ["identity"] = "IDENTITY.md",
        ["user"] = "USER.md",
        ["tools"] = "TOOLS.md",
        ["voice"] = "VOICE.md"
    };

    /// <summary>
    /// Maps GET/POST /api/admin/system-prompt for reading and editing prompt files.
    /// </summary>
    public static void MapSystemPromptEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/admin/system-prompt").RequireAuthorization();

        // GET — return current content of all 6 prompt files
        group.MapGet("/", (SystemPromptBuilder promptBuilder) =>
        {
            var dataPath = promptBuilder.DataPath;

            var result = new Dictionary<string, string?>();
            foreach (var (key, fileName) in KeyToFile)
            {
                var filePath = Path.Combine(dataPath, fileName);
                result[key] = File.Exists(filePath) ? File.ReadAllText(filePath).Trim() : null;
            }

            return Results.Ok(result);
        });

        // POST /reload — re-read all prompt files and skills from disk
        group.MapPost("/reload", (SystemPromptBuilder promptBuilder) =>
        {
            promptBuilder.Reload();
            return Results.NoContent();
        });

        // POST — partial update, only included keys are written
        group.MapPost("/", (JsonElement body, SystemPromptBuilder promptBuilder) =>
        {
            var dataPath = promptBuilder.DataPath;

            foreach (var (key, fileName) in KeyToFile)
            {
                if (!body.TryGetProperty(key, out var value)) continue;

                var filePath = Path.Combine(dataPath, fileName);
                var content = value.GetString();

                if (content is not null)
                    File.WriteAllText(filePath, content);
            }

            // Reload cached prompt content
            promptBuilder.Reload();

            return Results.NoContent();
        });
    }
}
