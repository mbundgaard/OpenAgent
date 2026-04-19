using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;

namespace OpenAgent.Security.ApiKey;

/// <summary>
/// Resolves the API key used by AddApiKeyAuth and persists it to {dataPath}/config/agent.json.
///
/// Resolution order (first hit wins):
///   1. ASP.NET configuration "Authentication:ApiKey" (env var Authentication__ApiKey,
///      appsettings.Development.json, command-line). When set, the value is also written
///      back to agent.json so the agent.json file is always the source of truth at rest.
///   2. Existing "apiKey" string property in agent.json.
///   3. Generate a new random key, persist it to agent.json.
///
/// Read-modify-write preserves all other top-level fields in agent.json.
/// </summary>
public static class ApiKeyResolver
{
    public const string ConfigKey = "Authentication:ApiKey";
    public const string JsonProperty = "apiKey";

    public static string Resolve(string dataPath, IConfiguration configuration)
    {
        var configPath = Path.Combine(dataPath, "config", "agent.json");
        var fromConfig = configuration[ConfigKey];
        var fromFile = ReadKey(configPath);

        if (!string.IsNullOrWhiteSpace(fromConfig))
        {
            if (!string.Equals(fromConfig, fromFile, StringComparison.Ordinal))
                WriteKey(configPath, fromConfig);
            return fromConfig;
        }

        if (!string.IsNullOrWhiteSpace(fromFile))
            return fromFile!;

        var generated = Generate();
        WriteKey(configPath, generated);
        return generated;
    }

    private static string Generate() => Convert.ToHexString(RandomNumberGenerator.GetBytes(24));

    private static string? ReadKey(string configPath)
    {
        if (!File.Exists(configPath))
            return null;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(configPath));
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty(JsonProperty, out var el)
                && el.ValueKind == JsonValueKind.String)
            {
                return el.GetString();
            }
        }
        catch (JsonException) { }
        return null;
    }

    private static void WriteKey(string configPath, string key)
    {
        JsonObject root;
        if (File.Exists(configPath))
        {
            try
            {
                var node = JsonNode.Parse(File.ReadAllText(configPath));
                root = node as JsonObject ?? new JsonObject();
            }
            catch (JsonException)
            {
                root = new JsonObject();
            }
        }
        else
        {
            root = new JsonObject();
        }

        root[JsonProperty] = key;

        Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
        File.WriteAllText(configPath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }
}
