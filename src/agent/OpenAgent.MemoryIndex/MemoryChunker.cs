using System.Text;
using System.Text.Json;
using OpenAgent.Contracts;
using OpenAgent.Models.Common;
using OpenAgent.Models.Configs;
using OpenAgent.Models.Conversations;

namespace OpenAgent.MemoryIndex;

/// <summary>
/// One topic chunk produced by the LLM — full body text and a one-line summary.
/// </summary>
public sealed record ChunkResult(string Content, string Summary);

/// <summary>
/// Uses the compaction LLM to split a raw daily memory file into self-contained topic
/// chunks, each with a one-line summary. Shape is fixed by the JSON response format.
/// </summary>
public sealed class MemoryChunker
{
    private const string SystemPrompt = """
        Restructure this daily memory log into self-contained topic chunks. For each chunk, provide the full content and a one-line summary.

        Rules:
        - Each chunk covers one topic or conversation thread
        - Each chunk must be understandable on its own, without the other chunks
        - The summary is a single sentence capturing the chunk's essence
        - Preserve all factual information — names, dates, decisions, URLs
        - Don't add information that wasn't in the original
        - Don't merge unrelated topics into one chunk
        - If the entire file is one topic, return a single chunk

        Output JSON:
        {
          "chunks": [
            { "content": "full chunk text", "summary": "one-line summary" },
            ...
          ]
        }
        """;

    private readonly Func<string, ILlmTextProvider> _providerFactory;
    private readonly AgentConfig _agentConfig;

    public MemoryChunker(Func<string, ILlmTextProvider> providerFactory, AgentConfig agentConfig)
    {
        _providerFactory = providerFactory;
        _agentConfig = agentConfig;
    }

    /// <summary>
    /// One LLM call per file. Uses the compaction provider + model configured on <see cref="AgentConfig"/>.
    /// Returns an empty list if the model produces no chunks.
    /// </summary>
    public async Task<IReadOnlyList<ChunkResult>> ChunkFileAsync(string fileContent, CancellationToken ct = default)
    {
        var messages = new List<Message>
        {
            new() { Id = "sys", ConversationId = "", Role = "system", Content = SystemPrompt },
            new() { Id = "usr", ConversationId = "", Role = "user", Content = fileContent },
        };

        var provider = _providerFactory(_agentConfig.CompactionProvider);
        var options = new CompletionOptions { ResponseFormat = "json_object" };

        var buffer = new StringBuilder();
        await foreach (var evt in provider.CompleteAsync(messages, _agentConfig.CompactionModel, options, ct))
        {
            if (evt is TextDelta delta)
                buffer.Append(delta.Content);
        }

        return ParseChunksResponse(buffer.ToString());
    }

    /// <summary>
    /// Pull the chunks array out of the model's JSON response. Tolerates missing keys —
    /// a response without the "chunks" field or with the field set to a non-array yields
    /// an empty list, matching the "no topics to index" contract.
    /// </summary>
    internal static IReadOnlyList<ChunkResult> ParseChunksResponse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return [];

        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("chunks", out var chunksEl) || chunksEl.ValueKind != JsonValueKind.Array)
            return [];

        var results = new List<ChunkResult>(chunksEl.GetArrayLength());
        foreach (var el in chunksEl.EnumerateArray())
        {
            var content = el.TryGetProperty("content", out var cEl) ? cEl.GetString() : null;
            var summary = el.TryGetProperty("summary", out var sEl) ? sEl.GetString() : null;
            if (string.IsNullOrEmpty(content) || string.IsNullOrEmpty(summary))
                continue;
            results.Add(new ChunkResult(content, summary));
        }
        return results;
    }
}
