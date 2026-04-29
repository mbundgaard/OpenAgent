using System.Text.Json;

namespace OpenAgent.App.Core.Models;

/// <summary>Snake-case JSON options matching the agent's wire format.</summary>
public static class JsonOptions
{
    public static readonly JsonSerializerOptions Default = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };
}
