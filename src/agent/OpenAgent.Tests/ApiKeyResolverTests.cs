using System.Text.Json;
using Microsoft.Extensions.Configuration;
using OpenAgent.Security.ApiKey;

namespace OpenAgent.Tests;

public class ApiKeyResolverTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _configPath;

    public ApiKeyResolverTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "openagent-apikey-" + Guid.NewGuid());
        Directory.CreateDirectory(_tempDir);
        _configPath = Path.Combine(_tempDir, "config", "agent.json");
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    [Fact]
    public void Resolve_WithConfigKeySet_ReturnsConfigKeyAndPersistsToFile()
    {
        var config = BuildConfig(new Dictionary<string, string?> { ["Authentication:ApiKey"] = "from-env" });

        var result = ApiKeyResolver.Resolve(_tempDir, config);

        Assert.Equal("from-env", result);
        Assert.Equal("from-env", ReadKeyFromFile());
    }

    [Fact]
    public void Resolve_WithConfigKeyDifferentFromFile_OverwritesFileWithConfigKey()
    {
        WriteAgentJson("""{"apiKey":"old-key","textProvider":"keep-me"}""");
        var config = BuildConfig(new Dictionary<string, string?> { ["Authentication:ApiKey"] = "new-key" });

        var result = ApiKeyResolver.Resolve(_tempDir, config);

        Assert.Equal("new-key", result);
        Assert.Equal("new-key", ReadKeyFromFile());

        // Other top-level fields are preserved.
        using var doc = JsonDocument.Parse(File.ReadAllText(_configPath));
        Assert.Equal("keep-me", doc.RootElement.GetProperty("textProvider").GetString());
    }

    [Fact]
    public void Resolve_WithConfigEmptyAndFileHasKey_ReturnsFileKey()
    {
        WriteAgentJson("""{"apiKey":"file-key"}""");
        var config = BuildConfig(new Dictionary<string, string?>());

        var result = ApiKeyResolver.Resolve(_tempDir, config);

        Assert.Equal("file-key", result);
    }

    [Fact]
    public void Resolve_WithNeitherConfigNorFile_GeneratesAndPersistsKey()
    {
        var config = BuildConfig(new Dictionary<string, string?>());

        var result = ApiKeyResolver.Resolve(_tempDir, config);

        Assert.False(string.IsNullOrWhiteSpace(result));
        Assert.Equal(48, result.Length); // 24 random bytes -> 48 hex chars
        Assert.Equal(result, ReadKeyFromFile());
    }

    [Fact]
    public void Resolve_WithExistingFileMissingApiKey_PreservesOtherFieldsAndAddsGeneratedKey()
    {
        WriteAgentJson("""{"symlinks":{"media":"D:\\Media"}}""");
        var config = BuildConfig(new Dictionary<string, string?>());

        var result = ApiKeyResolver.Resolve(_tempDir, config);

        Assert.False(string.IsNullOrWhiteSpace(result));
        using var doc = JsonDocument.Parse(File.ReadAllText(_configPath));
        Assert.Equal(result, doc.RootElement.GetProperty("apiKey").GetString());
        Assert.Equal("D:\\Media",
            doc.RootElement.GetProperty("symlinks").GetProperty("media").GetString());
    }

    [Fact]
    public void Resolve_TwoCallsWithoutConfigOrEnv_ReturnsSameKey()
    {
        var config = BuildConfig(new Dictionary<string, string?>());

        var first = ApiKeyResolver.Resolve(_tempDir, config);
        var second = ApiKeyResolver.Resolve(_tempDir, config);

        Assert.Equal(first, second);
    }

    private static IConfiguration BuildConfig(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    private void WriteAgentJson(string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_configPath)!);
        File.WriteAllText(_configPath, content);
    }

    private string? ReadKeyFromFile()
    {
        if (!File.Exists(_configPath)) return null;
        using var doc = JsonDocument.Parse(File.ReadAllText(_configPath));
        return doc.RootElement.TryGetProperty("apiKey", out var el) ? el.GetString() : null;
    }
}
