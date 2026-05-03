using System.Text.Json;
using OpenAgent.App.Core.Models;

namespace OpenAgent.App.Core.Services;

/// <summary>Per-connection filesystem cache for conversation lists. Each connection gets its own file.</summary>
public sealed class ConversationCache
{
    private readonly string _baseDir;

    public ConversationCache(string baseDirectory) => _baseDir = baseDirectory;

    /// <summary>Reads the cached conversation list for a connection, or null if no cache exists.</summary>
    public async Task<List<ConversationListItem>?> ReadAsync(string connectionId, CancellationToken ct = default)
    {
        try
        {
            var path = PathFor(connectionId);
            if (!File.Exists(path)) return null;
            await using var stream = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync<List<ConversationListItem>>(stream, JsonOptions.Default, ct);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Writes the conversation list cache for a connection.</summary>
    public async Task WriteAsync(string connectionId, IReadOnlyList<ConversationListItem> items, CancellationToken ct = default)
    {
        var path = PathFor(connectionId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        await using (var stream = File.Create(tmp))
        {
            await JsonSerializer.SerializeAsync(stream, items, JsonOptions.Default, ct);
        }
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>Removes the cache file for a connection.</summary>
    public void DeleteCache(string connectionId)
    {
        var path = PathFor(connectionId);
        try { File.Delete(path); } catch { }
    }

    private string PathFor(string connectionId) =>
        Path.Combine(_baseDir, $"conversations-{connectionId}.cache.json");
}
