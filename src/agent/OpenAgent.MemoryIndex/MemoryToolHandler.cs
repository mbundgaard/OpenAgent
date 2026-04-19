using System.Text.Json;
using OpenAgent.Contracts;
using OpenAgent.Models.Configs;

namespace OpenAgent.MemoryIndex;

/// <summary>
/// Exposes `search_memory` and `load_memory_chunks` tools. The tools are always advertised
/// (registration happens at DI-resolution time, which can fire before AgentConfig is loaded —
/// reading config state in the ctor leaves us with stale defaults). When the tools are
/// invoked without an embedding provider configured, they return a clear error to the LLM
/// rather than silently doing nothing.
/// </summary>
public sealed class MemoryToolHandler : IToolHandler
{
    public IReadOnlyList<ITool> Tools { get; }

    public MemoryToolHandler(MemoryIndexService service, AgentConfig agentConfig)
    {
        Tools = [
            new SearchMemoryTool(service, agentConfig),
            new LoadMemoryChunksTool(service, agentConfig),
        ];
    }
}

/// <summary>
/// Returns lightweight summaries ranked by hybrid vector+keyword score. The agent
/// scans these and calls load_memory_chunks for the ones it actually wants to read.
/// </summary>
internal sealed class SearchMemoryTool(MemoryIndexService service, AgentConfig agentConfig) : ITool
{
    private const int DefaultLimit = 5;

    public AgentToolDefinition Definition { get; } = new()
    {
        Name = "search_memory",
        Description = "Search your older daily memories. Call this proactively — BEFORE saying you don't remember or don't know — whenever the user references prior conversations, past events, people or places mentioned earlier, or anything likely to live in notes that aren't in the current prompt. The recent daily logs in your system prompt cover only the last few days; older material is reachable only through this tool. Returns lightweight summaries and ids; follow up with load_memory_chunks on promising hits to read the full text.",
        Parameters = new
        {
            type = "object",
            properties = new
            {
                query = new { type = "string", description = "Free-text query. Matched by hybrid semantic + keyword search. Phrase it like you'd recall a memory — names, topics, or distinctive phrases from what the user just said work well." },
                limit = new { type = "integer", description = $"Maximum number of results to return (default {DefaultLimit})." },
            },
            required = new[] { "query" },
        },
    };

    public async Task<string> ExecuteAsync(string arguments, string conversationId, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(agentConfig.EmbeddingProvider))
            return JsonSerializer.Serialize(new { error = "memory index disabled: embeddingProvider is not configured" });

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
internal sealed class LoadMemoryChunksTool(MemoryIndexService service, AgentConfig agentConfig) : ITool
{
    public AgentToolDefinition Definition { get; } = new()
    {
        Name = "load_memory_chunks",
        Description = "Load the full text of specific memory chunks by id. Use this when a search_memory summary looks relevant and you need the underlying detail to answer accurately, or when you want to quote the original wording.",
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
        if (string.IsNullOrEmpty(agentConfig.EmbeddingProvider))
            return JsonSerializer.Serialize(new { error = "memory index disabled: embeddingProvider is not configured" });

        var args = JsonDocument.Parse(arguments).RootElement;
        var idsEl = args.GetProperty("ids");
        var ids = new List<int>(idsEl.GetArrayLength());
        foreach (var el in idsEl.EnumerateArray())
            ids.Add(el.GetInt32());

        var chunks = await service.LoadChunksAsync(ids, ct);
        return JsonSerializer.Serialize(new { chunks });
    }
}
