using System.Text.Json;
using Microsoft.Extensions.Logging;
using OpenAgent.Contracts;

namespace OpenAgent.ConfigStore.File;

/// <summary>
/// Persists provider configurations as individual JSON files in a directory on disk.
/// </summary>
public sealed class FileConfigStore : IConfigStore
{
    private readonly string _directory;
    private readonly ILogger<FileConfigStore> _logger;

    public FileConfigStore(AgentEnvironment environment, ILogger<FileConfigStore> logger)
    {
        _directory = Path.Combine(environment.DataPath, "config");
        _logger = logger;
        Directory.CreateDirectory(_directory);
        _logger.LogInformation("Config store using directory {ConfigDirectory}", _directory);
    }

    public JsonElement? Load(string key)
    {
        var path = Path.Combine(_directory, $"{key}.json");
        if (!System.IO.File.Exists(path))
        {
            _logger.LogDebug("No config found for {Key}", key);
            return null;
        }

        var bytes = System.IO.File.ReadAllBytes(path);
        using var doc = JsonDocument.Parse(bytes);
        _logger.LogInformation("Loaded config for {Key}", key);
        return doc.RootElement.Clone();
    }

    public void Save(string key, JsonElement config)
    {
        var path = Path.Combine(_directory, $"{key}.json");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(config, new JsonSerializerOptions { WriteIndented = true });
        System.IO.File.WriteAllBytes(path, bytes);
        _logger.LogInformation("Saved config for {Key}", key);
    }
}
