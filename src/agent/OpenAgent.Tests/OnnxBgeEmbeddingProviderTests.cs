using OpenAgent.Contracts;
using OpenAgent.Embedding.OnnxBge;
using OpenAgent.Models.Configs;

namespace OpenAgent.Tests;

public class OnnxBgeEmbeddingProviderTests
{
    [Fact]
    public void L2Normalize_produces_unit_length_vector()
    {
        var input = new float[] { 3, 4, 0 };
        var normalized = OnnxBgeEmbeddingProvider.L2Normalize(input);

        double magSquared = 0;
        foreach (var v in normalized)
            magSquared += v * v;

        Assert.Equal(1.0, magSquared, precision: 5);
        Assert.Equal(0.6f, normalized[0], precision: 5);
        Assert.Equal(0.8f, normalized[1], precision: 5);
    }

    [Fact]
    public void L2Normalize_zero_vector_returns_zero_without_nan()
    {
        var normalized = OnnxBgeEmbeddingProvider.L2Normalize([0f, 0f, 0f]);
        foreach (var v in normalized)
            Assert.False(float.IsNaN(v));
        Assert.Equal([0f, 0f, 0f], normalized);
    }

    [Fact]
    public void Constructor_sets_key_and_reports_dimensions_per_model()
    {
        using var tempDir = new TempDataDir();
        var env = new AgentEnvironment { DataPath = tempDir.Path };

        using var small = new OnnxBgeEmbeddingProvider(env, new AgentConfig { EmbeddingModel = "bge-small-en-v1.5" });
        using var baseP = new OnnxBgeEmbeddingProvider(env, new AgentConfig { EmbeddingModel = "bge-base-en-v1.5" });
        using var large = new OnnxBgeEmbeddingProvider(env, new AgentConfig { EmbeddingModel = "bge-large-en-v1.5" });

        Assert.Equal("bge", small.Key);
        Assert.Equal("bge-small-en-v1.5", small.Model);
        Assert.Equal(384, small.Dimensions);

        Assert.Equal(768, baseP.Dimensions);
        Assert.Equal(1024, large.Dimensions);
    }

    [Fact]
    public void Constructor_rejects_unknown_model()
    {
        using var tempDir = new TempDataDir();
        var env = new AgentEnvironment { DataPath = tempDir.Path };

        Assert.Throws<InvalidOperationException>(() =>
            new OnnxBgeEmbeddingProvider(env, new AgentConfig { EmbeddingModel = "not-a-bge-model" }));
    }

    [Fact]
    public async Task GenerateEmbeddingAsync_surfaces_missing_files_on_first_use()
    {
        using var tempDir = new TempDataDir();
        var env = new AgentEnvironment { DataPath = tempDir.Path };
        using var provider = new OnnxBgeEmbeddingProvider(env, new AgentConfig { EmbeddingModel = "bge-base-en-v1.5" });

        await Assert.ThrowsAnyAsync<Exception>(
            () => provider.GenerateEmbeddingAsync("hello", EmbeddingPurpose.Indexing));
    }

    // End-to-end — only runs when the real BGE model files are present on disk.
    [Fact]
    public async Task GenerateEmbeddingAsync_returns_unit_vector_and_distinguishes_query_and_passage()
    {
        var dataDir = LocateDataDirWithModels();
        if (dataDir is null) return; // skip — files not present

        var env = new AgentEnvironment { DataPath = dataDir };
        var config = new AgentConfig { EmbeddingModel = "bge-base-en-v1.5" };

        using var provider = new OnnxBgeEmbeddingProvider(env, config);

        var passage = await provider.GenerateEmbeddingAsync("The quick brown fox jumps over the lazy dog.", EmbeddingPurpose.Indexing);
        var query = await provider.GenerateEmbeddingAsync("The quick brown fox jumps over the lazy dog.", EmbeddingPurpose.Search);

        Assert.Equal(provider.Dimensions, passage.Length);
        Assert.Equal(provider.Dimensions, query.Length);

        double magPassage = 0, magQuery = 0;
        for (var i = 0; i < passage.Length; i++)
        {
            magPassage += passage[i] * passage[i];
            magQuery += query[i] * query[i];
        }
        Assert.Equal(1.0, magPassage, precision: 3);
        Assert.Equal(1.0, magQuery, precision: 3);

        // Query gets the instruction prefix, passage doesn't — embeddings must differ
        var cosine = CosineSimilarity(passage, query);
        Assert.True(cosine < 0.999f, $"query/passage should differ; cosine was {cosine}");
    }

    private static string? LocateDataDirWithModels()
    {
        var envOverride = Environment.GetEnvironmentVariable("OPENAGENT_BGE_DATA_DIR");
        if (!string.IsNullOrEmpty(envOverride)
            && File.Exists(Path.Combine(envOverride, "models", "bge-base-en-v1.5", "model.onnx")))
            return envOverride;

        var dir = AppContext.BaseDirectory;
        for (var depth = 0; depth < 8 && dir is not null; depth++)
        {
            var candidate = Path.Combine(dir, "models", "bge-base-en-v1.5");
            if (File.Exists(Path.Combine(candidate, "model.onnx"))
                && File.Exists(Path.Combine(candidate, "vocab.txt")))
                return dir;
            dir = Path.GetDirectoryName(dir);
        }
        return null;
    }

    private static float CosineSimilarity(float[] a, float[] b)
    {
        double dot = 0, magA = 0, magB = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            magA += a[i] * a[i];
            magB += b[i] * b[i];
        }
        if (magA == 0 || magB == 0) return 0;
        return (float)(dot / (Math.Sqrt(magA) * Math.Sqrt(magB)));
    }

    private sealed class TempDataDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"bge-test-{Guid.NewGuid()}");
        public TempDataDir() => Directory.CreateDirectory(Path);
        public void Dispose() { try { Directory.Delete(Path, true); } catch { } }
    }
}
