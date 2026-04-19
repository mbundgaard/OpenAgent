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
/// Outcome of chunking one file. Either the LLM split it into chunks to index,
/// or it flagged the file as unimportant and requested deletion without indexing.
/// An empty chunks list with Discard=false means "can't chunk right now" and the
/// service will leave the file on disk for the next run.
/// </summary>
public sealed record ChunkFileOutcome(IReadOnlyList<ChunkResult> Chunks, bool Discard);

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
        - If the file contains nothing worth preserving — empty, trivial scratch, corrupted data, duplicate of obvious facts — set "discard" to true and return an empty chunks array. The file will be deleted without indexing. Use only when there is genuinely nothing of lasting value; prefer to chunk even short or fragmentary notes when they contain any real information.

        Output JSON:
        {
          "chunks": [
            { "content": "full chunk text", "summary": "one-line summary" },
            ...
          ],
          "discard": false
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
    /// Returns the parsed outcome — either chunks to index, or a discard flag when the model
    /// decides the file isn't worth preserving.
    /// </summary>
    public async Task<ChunkFileOutcome> ChunkFileAsync(string fileContent, CancellationToken ct = default)
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
    /// Pull the chunks array and discard flag out of the model's JSON response. Tolerates
    /// missing keys — a response without "chunks" or with a non-array value yields an empty
    /// list; a missing "discard" defaults to false. Also tolerates a leading markdown code
    /// fence that some providers emit even when asked for structured output — Anthropic in
    /// particular doesn't enforce strict JSON mode.
    /// </summary>
    internal static ChunkFileOutcome ParseChunksResponse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return new ChunkFileOutcome([], Discard: false);

        // Carve the JSON object out of any surrounding fence/prose. Prefer the span from the
        // first `{` to the last `}` — simple, resilient to language tags and trailing commentary.
        var start = json.IndexOf('{');
        var end = json.LastIndexOf('}');
        if (start < 0 || end <= start)
            return new ChunkFileOutcome([], Discard: false);
        var payload = json[start..(end + 1)];

        using var doc = JsonDocument.Parse(payload);

        var discard = doc.RootElement.TryGetProperty("discard", out var dEl)
            && dEl.ValueKind == JsonValueKind.True;

        if (!doc.RootElement.TryGetProperty("chunks", out var chunksEl) || chunksEl.ValueKind != JsonValueKind.Array)
            return new ChunkFileOutcome([], discard);

        var results = new List<ChunkResult>(chunksEl.GetArrayLength());
        foreach (var el in chunksEl.EnumerateArray())
        {
            var content = el.TryGetProperty("content", out var cEl) ? cEl.GetString() : null;
            var summary = el.TryGetProperty("summary", out var sEl) ? sEl.GetString() : null;
            if (string.IsNullOrEmpty(content) || string.IsNullOrEmpty(summary))
                continue;
            results.Add(new ChunkResult(content, summary));
        }
        return new ChunkFileOutcome(results, discard);
    }
}
