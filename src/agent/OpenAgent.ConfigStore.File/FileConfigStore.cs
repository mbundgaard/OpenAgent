using System.Text.Json;
using OpenAgent.Contracts;

namespace OpenAgent.ConfigStore.File;

/// <summary>
/// Persists provider configurations as individual JSON files in a directory on disk.
/// </summary>
public sealed class FileConfigStore : IConfigStore
{
    private readonly string _directory;

    public FileConfigStore(string contentRootPath)
    {
        _directory = Path.Combine(contentRootPath, "config");
        Directory.CreateDirectory(_directory);
    }

    public JsonElement? Load(string key)
    {
        var path = Path.Combine(_directory, $"{key}.json");
        if (!System.IO.File.Exists(path))
            return null;

        var bytes = System.IO.File.ReadAllBytes(path);
        using var doc = JsonDocument.Parse(bytes);
        return doc.RootElement.Clone();
    }

    public void Save(string key, JsonElement config)
    {
        var path = Path.Combine(_directory, $"{key}.json");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(config, new JsonSerializerOptions { WriteIndented = true });
        System.IO.File.WriteAllBytes(path, bytes);
    }
}
