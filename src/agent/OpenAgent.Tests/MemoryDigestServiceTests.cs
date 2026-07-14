using Microsoft.Extensions.Logging.Abstractions;
using OpenAgent.Contracts;
using OpenAgent.MemoryDigest;
using OpenAgent.Models.Configs;
using OpenAgent.Tests.Fakes;

namespace OpenAgent.Tests;

public class MemoryDigestServiceTests : IDisposable
{
    private readonly string _dataPath;
    private readonly AgentEnvironment _environment;

    public MemoryDigestServiceTests()
    {
        _dataPath = Path.Combine(Path.GetTempPath(), "openagent-digest-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dataPath);
        Directory.CreateDirectory(Path.Combine(_dataPath, "memory"));
        _environment = new AgentEnvironment { DataPath = _dataPath };
    }

    public void Dispose()
    {
        try { Directory.Delete(_dataPath, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private MemoryDigestService BuildService(AgentConfig config, ILlmTextProvider provider)
    {
        Func<string, ILlmTextProvider> factory = _ => provider;
        return new MemoryDigestService(factory, config, _environment, NullLogger<MemoryDigestService>.Instance);
    }

    private static AgentConfig ConfigWithCompaction() => new()
    {
        CompactionProvider = "fake",
        CompactionModel = "test-model"
    };

    private void WriteDigestInstructions(string content = "Output JSON.") =>
        File.WriteAllText(Path.Combine(_dataPath, "DIGEST.md"), content);

    private void WriteMemory(string content) =>
        File.WriteAllText(Path.Combine(_dataPath, "MEMORY.md"), content);

    private void WriteDailyLog(string date, string content) =>
        File.WriteAllText(Path.Combine(_dataPath, "memory", $"{date}.md"), content);

    [Fact]
    public async Task Skips_when_compaction_provider_is_unset()
    {
        WriteDigestInstructions();
        var service = BuildService(new AgentConfig(), new StreamingTextProvider("{}"));

        var result = await service.RunAsync();

        Assert.False(result.Ran);
        Assert.True(result.Skipped);
        Assert.Contains("provider", result.Reason!);
    }

    [Fact]
    public async Task Skips_when_DIGEST_md_missing()
    {
        var service = BuildService(ConfigWithCompaction(), new StreamingTextProvider("{}"));

        var result = await service.RunAsync();

        Assert.False(result.Ran);
        Assert.Contains("DIGEST.md", result.Reason!);
    }

    [Fact]
    public async Task Skips_when_audit_file_already_exists_for_today()
    {
        WriteDigestInstructions();
        WriteMemory("## Notes\n\nx\n");
        var digestsDir = Path.Combine(_dataPath, "memory", "digests");
        Directory.CreateDirectory(digestsDir);
        var today = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow,
            TimeZoneInfo.FindSystemTimeZoneById("Europe/Copenhagen")).ToString("yyyy-MM-dd");
        File.WriteAllText(Path.Combine(digestsDir, $"{today}.json"), "{}");

        var service = BuildService(ConfigWithCompaction(), new StreamingTextProvider("{}"));

        var result = await service.RunAsync();

        Assert.False(result.Ran);
        Assert.Contains("already", result.Reason!);
    }

    [Fact]
    public async Task Applies_add_operation_and_writes_audit()
    {
        WriteDigestInstructions();
        WriteMemory("## Martin\n\nLikes coffee.\n");
        WriteDailyLog("2026-05-30", "Talked about hiking.");

        var llmResponse = """
            {
              "date": "2026-05-31",
              "operations": [
                { "action": "add", "section": "Martin", "content": "Started hiking again." }
              ]
            }
            """;
        var service = BuildService(ConfigWithCompaction(), new StreamingTextProvider(llmResponse));

        var result = await service.RunAsync();

        Assert.True(result.Ran);
        Assert.Equal(1, result.OperationsAttempted);
        Assert.Equal(1, result.OperationsApplied);

        var memory = await File.ReadAllTextAsync(Path.Combine(_dataPath, "MEMORY.md"));
        Assert.Contains("Started hiking again.", memory);
        Assert.Contains("Likes coffee.", memory);

        var auditFiles = Directory.GetFiles(Path.Combine(_dataPath, "memory", "digests"), "*.json");
        Assert.Single(auditFiles);
    }

    [Fact]
    public async Task Empty_operations_array_leaves_memory_untouched_but_writes_audit()
    {
        WriteDigestInstructions();
        var original = "## Notes\n\nUntouched.\n";
        WriteMemory(original);
        var llmResponse = """{"date":"2026-05-31","operations":[]}""";

        var service = BuildService(ConfigWithCompaction(), new StreamingTextProvider(llmResponse));

        var result = await service.RunAsync();

        Assert.True(result.Ran);
        Assert.Equal(0, result.OperationsAttempted);
        Assert.Equal(original, await File.ReadAllTextAsync(Path.Combine(_dataPath, "MEMORY.md")));
        Assert.Single(Directory.GetFiles(Path.Combine(_dataPath, "memory", "digests"), "*.json"));
    }

    [Fact]
    public async Task Tolerates_fenced_json_response()
    {
        WriteDigestInstructions();
        WriteMemory("## Notes\n\nx\n");

        var llmResponse = "```json\n{\"date\":\"2026-05-31\",\"operations\":[{\"action\":\"add\",\"section\":\"Notes\",\"content\":\"y\"}]}\n```";
        var service = BuildService(ConfigWithCompaction(), new StreamingTextProvider(llmResponse));

        var result = await service.RunAsync();

        Assert.True(result.Ran);
        Assert.Equal(1, result.OperationsApplied);
        Assert.Contains("y", await File.ReadAllTextAsync(Path.Combine(_dataPath, "MEMORY.md")));
    }

    [Fact]
    public async Task Records_parse_error_and_does_not_touch_memory()
    {
        WriteDigestInstructions();
        var original = "## Notes\n\nUntouched.\n";
        WriteMemory(original);

        var service = BuildService(ConfigWithCompaction(), new StreamingTextProvider("not even close to json"));

        var result = await service.RunAsync();

        Assert.True(result.Ran);
        Assert.NotNull(result.Reason);
        Assert.Equal(original, await File.ReadAllTextAsync(Path.Combine(_dataPath, "MEMORY.md")));

        var auditPath = Directory.GetFiles(Path.Combine(_dataPath, "memory", "digests"), "*.json").Single();
        var audit = await File.ReadAllTextAsync(auditPath);
        Assert.Contains("not even close to json", audit);
        Assert.Contains("error", audit);
    }

    [Fact]
    public async Task Partial_application_records_per_op_results()
    {
        WriteDigestInstructions();
        WriteMemory("## Martin\n\nLikes coffee.\n");

        var llmResponse = """
            {
              "operations": [
                { "action": "add", "section": "Martin", "content": "Note 1." },
                { "action": "update", "section": "Phantom", "old": "x", "new": "y" }
              ]
            }
            """;
        var service = BuildService(ConfigWithCompaction(), new StreamingTextProvider(llmResponse));

        var result = await service.RunAsync();

        Assert.Equal(2, result.OperationsAttempted);
        Assert.Equal(1, result.OperationsApplied);

        var auditPath = Directory.GetFiles(Path.Combine(_dataPath, "memory", "digests"), "*.json").Single();
        var audit = await File.ReadAllTextAsync(auditPath);
        Assert.Contains("not found", audit);
    }

    [Fact]
    public async Task Loads_at_most_seven_daily_logs()
    {
        WriteDigestInstructions();
        WriteMemory("## Notes\n\nx\n");
        // 10 logs — only the newest 7 should reach the LLM, which we verify via the captor.
        for (var i = 1; i <= 10; i++)
            WriteDailyLog($"2026-05-{i:D2}", $"log {i}");

        var captor = new CapturingTextProvider("""{"operations":[]}""");
        var service = BuildService(ConfigWithCompaction(), captor);

        await service.RunAsync();

        // The 7 newest are 04..10, identified by their date attribute
        Assert.Contains("date=\"2026-05-10\"", captor.LastUserContent);
        Assert.Contains("date=\"2026-05-04\"", captor.LastUserContent);
        Assert.DoesNotContain("date=\"2026-05-03\"", captor.LastUserContent);
        Assert.DoesNotContain("date=\"2026-05-01\"", captor.LastUserContent);
    }

    [Fact]
    public void ParseOperations_handles_balanced_json_inside_prose()
    {
        var raw = "Sure, here you go:\n\n{\"date\":\"2026-05-31\",\"operations\":[{\"action\":\"add\",\"section\":\"X\",\"content\":\"y\"}]}\n\nLet me know.";
        var (date, ops, err) = MemoryDigestService.ParseOperations(raw);
        Assert.Null(err);
        Assert.Equal("2026-05-31", date);
        Assert.Single(ops);
        Assert.Equal(DigestOperationKind.Add, ops[0].Action);
    }
}

/// <summary>
/// Captures the user message content sent to the provider so tests can assert the prompt
/// payload. Returns a configured raw response.
/// </summary>
internal sealed class CapturingTextProvider : ILlmTextProvider
{
    private readonly string _response;
    public string LastUserContent { get; private set; } = "";
    public string LastSystemContent { get; private set; } = "";

    public CapturingTextProvider(string response) { _response = response; }

    public string Key => "capturing";
    public IReadOnlyList<OpenAgent.Models.Providers.ProviderConfigField> ConfigFields => [];
    public void Configure(System.Text.Json.JsonElement configuration) { }
    public int? GetContextWindow(string model) => null;

    public async IAsyncEnumerable<OpenAgent.Models.Common.CompletionEvent> CompleteAsync(
        OpenAgent.Models.Conversations.Conversation conversation,
        OpenAgent.Models.Conversations.Message userMessage,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default,
        bool persistUserMessage = true)
    {
        LastUserContent = userMessage.Content;
        yield return new OpenAgent.Models.Common.TextDelta(_response);
        await Task.Yield();
    }

    public async IAsyncEnumerable<OpenAgent.Models.Common.CompletionEvent> CompleteAsync(
        IReadOnlyList<OpenAgent.Models.Conversations.Message> messages,
        string model,
        OpenAgent.Models.Common.CompletionOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        LastSystemContent = messages.FirstOrDefault(m => m.Role == "system")?.Content ?? "";
        LastUserContent = messages.FirstOrDefault(m => m.Role == "user")?.Content ?? "";
        yield return new OpenAgent.Models.Common.TextDelta(_response);
        await Task.Yield();
    }
}
