using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using OpenAgent.Contracts;
using OpenAgent.Models.Conversations;

namespace OpenAgent.Compaction;

/// <summary>
/// Calls the LLM to generate a structured compaction summary from conversation messages.
/// </summary>
public sealed class CompactionSummarizer : ICompactionSummarizer, IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly string _url;
    private readonly ILogger<CompactionSummarizer> _logger;

    public CompactionSummarizer(CompactionLlmConfig config, ILogger<CompactionSummarizer> logger)
    {
        _logger = logger;
        _httpClient = new HttpClient
        {
            BaseAddress = new Uri(config.Endpoint.TrimEnd('/') + "/")
        };
        _httpClient.DefaultRequestHeaders.Add("api-key", config.ApiKey);
        _url = $"openai/deployments/{config.DeploymentName}/chat/completions?api-version={config.ApiVersion}";
    }

    public async Task<CompactionResult> SummarizeAsync(string? existingContext, IReadOnlyList<Message> messages, CancellationToken ct = default)
    {
        var userContent = new System.Text.StringBuilder();

        if (existingContext is not null)
        {
            userContent.AppendLine("## Existing Context (from previous compaction)");
            userContent.AppendLine(existingContext);
            userContent.AppendLine();
        }

        userContent.AppendLine("## Messages to Compact");
        foreach (var msg in messages)
        {
            userContent.AppendLine($"[{msg.Id}] [{msg.CreatedAt:yyyy-MM-dd HH:mm}] [{msg.Role}]: {msg.Content}");
            if (msg.ToolCalls is not null)
                userContent.AppendLine($"  Tool calls: {msg.ToolCalls}");
            if (msg.ToolCallId is not null)
                userContent.AppendLine($"  (tool result for call {msg.ToolCallId})");
        }

        var request = new
        {
            messages = new object[]
            {
                new { role = "system", content = CompactionPrompt.System },
                new { role = "user", content = userContent.ToString() }
            },
            response_format = new { type = "json_object" }
        };

        var response = await _httpClient.PostAsJsonAsync(_url, request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("Compaction LLM call failed: {StatusCode} {Body}", (int)response.StatusCode, body);
            throw new HttpRequestException($"Compaction LLM call failed: {(int)response.StatusCode}");
        }

        using var doc = JsonDocument.Parse(body);
        var content = doc.RootElement
            .GetProperty("choices")[0]
            .GetProperty("message")
            .GetProperty("content")
            .GetString()!;

        using var resultDoc = JsonDocument.Parse(content);
        var context = resultDoc.RootElement.GetProperty("context").GetString()!;
        var memories = resultDoc.RootElement.TryGetProperty("memories", out var mem)
            ? mem.EnumerateArray().Select(m => m.GetString()!).ToList()
            : new List<string>();

        _logger.LogInformation("Compaction summary generated: {Length} chars, {MemoryCount} memories", context.Length, memories.Count);

        return new CompactionResult { Context = context, Memories = memories };
    }

    public void Dispose() => _httpClient.Dispose();
}
