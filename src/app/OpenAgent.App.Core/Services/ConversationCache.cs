using System.Text.Json;
using OpenAgent.App.Core.Models;

namespace OpenAgent.App.Core.Services;

/// <summary>Filesystem-backed JSON cache for the conversation list, used to render the home screen instantly while the network refresh is in flight.</summary>
public sealed class ConversationCache
{
    private readonly string _path;

    public ConversationCache(string baseDirectory)
    {
        _path = Path.Combine(baseDirectory, "conversations.cache.json");
    }

    public async Task<List<ConversationListItem>?> ReadAsync(CancellationToken ct = default)
    {
        try
        {
            if (!File.Exists(_path)) return null;
            await using var stream = File.OpenRead(_path);
            return await JsonSerializer.DeserializeAsync<List<ConversationListItem>>(stream, JsonOptions.Default, ct);
        }
        catch
        {
            return null;
        }
    }

    public async Task WriteAsync(IReadOnlyList<ConversationListItem> items, CancellationToken ct = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var tmp = _path + ".tmp";
        await using (var stream = File.Create(tmp))
        {
            await JsonSerializer.SerializeAsync(stream, items, JsonOptions.Default, ct);
        }
        File.Move(tmp, _path, overwrite: true);
    }
}
