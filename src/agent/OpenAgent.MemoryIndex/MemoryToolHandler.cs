using System.Text.Json;
using OpenAgent.Contracts;
using OpenAgent.Models.Configs;

namespace OpenAgent.MemoryIndex;

/// <summary>
/// Exposes `search_memory` and `load_memory_chunks` tools. Tools are only advertised
/// when an embedding provider is configured — with it unset the memory index is
/// essentially offline, so there's nothing for the agent to search or load.
/// </summary>
public sealed class MemoryToolHandler : IToolHandler
{
    public IReadOnlyList<ITool> Tools { get; }

    public MemoryToolHandler(MemoryIndexService service, AgentConfig agentConfig)
    {
        Tools = string.IsNullOrEmpty(agentConfig.EmbeddingProvider)
            ? []
            : [new SearchMemoryTool(service), new LoadMemoryChunksTool(service)];
    }
}

/// <summary>
/// Returns lightweight summaries ranked by hybrid vector+keyword score. The agent
/// scans these and calls load_memory_chunks for the ones it actually wants to read.
/// </summary>
internal sealed class SearchMemoryTool(MemoryIndexService service) : ITool
{
    private const int DefaultLimit = 5;

    public AgentToolDefinition Definition { get; } = new()
    {
        Name = "search_memory",
        Description = "Search older daily memories by topic. Returns lightweight summaries with ids you can pass to load_memory_chunks to read full content.",
        Parameters = new
        {
            type = "object",
            properties = new
            {
                query = new { type = "string", description = "Free-text query. Matched by hybrid semantic + keyword search." },
                limit = new { type = "integer", description = $"Maximum number of results to return (default {DefaultLimit})." },
            },
            required = new[] { "query" },
        },
    };

    public async Task<string> ExecuteAsync(string arguments, string conversationId, CancellationToken ct = default)
    {
        var args = JsonDocument.Parse(arguments).RootElement;
        var query = args.GetProperty("query").GetString() ?? throw new ArgumentException("query is required");
        var limit = args.TryGetProperty("limit", out var lEl) && lEl.ValueKind == JsonValueKind.Number
            ? Math.Max(1, lEl.GetInt32())
            : DefaultLimit;

        var results = await service.SearchAsync(query, limit, ct);
        return JsonSerializer.Serialize(new { results });
    }
}

/// <summary>
/// Loads the full content of specific chunks by id. Paired with search_memory —
/// the agent chooses which summaries are worth expanding and loads just those.
/// </summary>
internal sealed class LoadMemoryChunksTool(MemoryIndexService service) : ITool
{
    public AgentToolDefinition Definition { get; } = new()
    {
        Name = "load_memory_chunks",
        Description = "Load full content for specific memory chunks by id. Use the ids returned by search_memory.",
        Parameters = new
        {
            type = "object",
            properties = new
            {
                ids = new
                {
                    type = "array",
                    items = new { type = "integer" },
                    description = "Chunk ids to load.",
                },
            },
            required = new[] { "ids" },
        },
    };

    public async Task<string> ExecuteAsync(string arguments, string conversationId, CancellationToken ct = default)
    {
        var args = JsonDocument.Parse(arguments).RootElement;
        var idsEl = args.GetProperty("ids");
        var ids = new List<int>(idsEl.GetArrayLength());
        foreach (var el in idsEl.EnumerateArray())
            ids.Add(el.GetInt32());

        var chunks = await service.LoadChunksAsync(ids, ct);
        return JsonSerializer.Serialize(new { chunks });
    }
}
