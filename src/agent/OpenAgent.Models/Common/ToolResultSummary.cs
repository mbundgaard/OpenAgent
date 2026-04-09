using System.Text.Json;

namespace OpenAgent.Models.Common;

/// <summary>
/// Creates compact summaries of tool results for persistence.
/// The full result is used by the LLM in the current turn but only the summary is stored.
/// </summary>
public static class ToolResultSummary
{
    /// <summary>
    /// Summarizes a tool result into a compact JSON string with tool name, status, and size.
    /// </summary>
    public static string Create(string toolName, string result)
    {
        // Check if the result is an error
        var isError = false;
        string? errorMessage = null;

        try
        {
            using var doc = JsonDocument.Parse(result);
            // Only object roots can carry an "error" field — arrays (e.g. list_scheduled_tasks)
            // and primitives are normal successful results
            if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                doc.RootElement.TryGetProperty("error", out var errorProp))
            {
                isError = true;
                errorMessage = errorProp.GetString();
            }
        }
        catch (JsonException)
        {
            // Not JSON — that's fine
        }

        if (isError)
        {
            return JsonSerializer.Serialize(new
            {
                tool = toolName,
                status = "error",
                error = errorMessage ?? result[..Math.Min(result.Length, 200)]
            });
        }

        return JsonSerializer.Serialize(new
        {
            tool = toolName,
            status = "ok",
            size = result.Length
        });
    }
}
