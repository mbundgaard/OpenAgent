using System.Text.Json.Serialization;

namespace OpenAgent.App.Core.Services;

/// <summary>A named server connection with credentials.</summary>
public sealed record ServerConnection(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("base_url")] string BaseUrl,
    [property: JsonPropertyName("token")] string Token);
