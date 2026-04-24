using Microsoft.Extensions.Logging.Abstractions;
using OpenAgent.Contracts;
using OpenAgent.Embedding.OnnxMultilingualE5;
using OpenAgent.Models.Configs;

namespace OpenAgent.Tests;

public class OnnxMultilingualE5EmbeddingProviderTests
{
    [Fact]
    public void MeanPool_averages_only_non_padding_positions()
    {
        // seq=4, hidden=3. First two positions are real (mask=1), last two are padding (mask=0).
        // Real embeddings: [1,2,3] and [3,4,5]. Expected mean: [2, 3, 4].
        // Padding positions contain large garbage values that must be ignored.
        var tokenEmbeddings = new float[]
        {
            1, 2, 3,          // position 0, real
            3, 4, 5,          // position 1, real
            1000, 2000, 3000, // position 2, padding — must not contribute
            -999, -999, -999, // position 3, padding — must not contribute
        };
        var mask = new long[] { 1, 1, 0, 0 };

        var pooled = OnnxMultilingualE5EmbeddingProvider.MeanPool(tokenEmbeddings, mask, hiddenSize: 3);

        Assert.Equal(3, pooled.Length);
        Assert.Equal(2f, pooled[0], precision: 5);
        Assert.Equal(3f, pooled[1], precision: 5);
        Assert.Equal(4f, pooled[2], precision: 5);
    }

    [Fact]
    public void MeanPool_all_padding_returns_zero_vector()
    {
        var tokenEmbeddings = new float[] { 1, 2, 3, 4, 5, 6 };
        var mask = new long[] { 0, 0 };

        var pooled = OnnxMultilingualE5EmbeddingProvider.MeanPool(tokenEmbeddings, mask, hiddenSize: 3);

        Assert.Equal([0f, 0f, 0f], pooled);
    }

    [Fact]
    public void L2Normalize_produces_unit_length_vector()
    {
        var input = new float[] { 3, 4, 0 }; // magnitude = 5
        var normalized = OnnxMultilingualE5EmbeddingProvider.L2Normalize(input);

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
        var normalized = OnnxMultilingualE5EmbeddingProvider.L2Normalize([0f, 0f, 0f]);

        foreach (var v in normalized)
            Assert.False(float.IsNaN(v), "zero input must not produce NaN");

        Assert.Equal([0f, 0f, 0f], normalized);
    }

    [Fact]
    public void Constructor_sets_key_model_and_dimensions_from_config()
    {
        using var tempDir = new TempDataDir();
        var env = new AgentEnvironment { DataPath = tempDir.Path };
        var config = new AgentConfig { EmbeddingModel = "multilingual-e5-small" };

        using var provider = new OnnxMultilingualE5EmbeddingProvider(env, config, NullLogger<OnnxMultilingualE5EmbeddingProvider>.Instance, autoDownload: false);

        Assert.Equal("multilingual-e5", provider.Key);
        Assert.Equal("multilingual-e5-small", provider.Model);
        Assert.Equal(384, provider.Dimensions);
    }

    [Fact]
    public void Constructor_reports_correct_dimensions_for_base()
    {
        using var tempDir = new TempDataDir();
        var env = new AgentEnvironment { DataPath = tempDir.Path };
        var config = new AgentConfig { EmbeddingModel = "multilingual-e5-base" };

        using var provider = new OnnxMultilingualE5EmbeddingProvider(env, config, NullLogger<OnnxMultilingualE5EmbeddingProvider>.Instance, autoDownload: false);

        Assert.Equal(768, provider.Dimensions);
    }

    [Fact]
    public void Constructor_reports_correct_dimensions_for_large()
    {
        using var tempDir = new TempDataDir();
        var env = new AgentEnvironment { DataPath = tempDir.Path };
        var config = new AgentConfig { EmbeddingModel = "multilingual-e5-large" };

        using var provider = new OnnxMultilingualE5EmbeddingProvider(env, config, NullLogger<OnnxMultilingualE5EmbeddingProvider>.Instance, autoDownload: false);

        Assert.Equal(1024, provider.Dimensions);
    }

    [Fact]
    public void Constructor_rejects_unknown_model()
    {
        using var tempDir = new TempDataDir();
        var env = new AgentEnvironment { DataPath = tempDir.Path };
        var config = new AgentConfig { EmbeddingModel = "not-a-real-model" };

        Assert.Throws<InvalidOperationException>(() => new OnnxMultilingualE5EmbeddingProvider(env, config, NullLogger<OnnxMultilingualE5EmbeddingProvider>.Instance, autoDownload: false));
    }

    [Fact]
    public async Task GenerateEmbeddingAsync_surfaces_missing_files_on_first_use()
    {
        // Construction is lazy — files are only required on first embed call.
        using var tempDir = new TempDataDir();
        var env = new AgentEnvironment { DataPath = tempDir.Path };
        var config = new AgentConfig { EmbeddingModel = "multilingual-e5-base" };

        using var provider = new OnnxMultilingualE5EmbeddingProvider(env, config, NullLogger<OnnxMultilingualE5EmbeddingProvider>.Instance, autoDownload: false);

        // Model dir doesn't exist under the temp dir, so first call must surface an error
        await Assert.ThrowsAnyAsync<Exception>(
            () => provider.GenerateEmbeddingAsync("anything", EmbeddingPurpose.Indexing));
    }

    // Full end-to-end inference. Runs only when the real model files are actually present.
    [Fact]
    public async Task GenerateEmbeddingAsync_returns_unit_vector_and_distinguishes_query_and_passage()
    {
        var dataDir = LocateDataDirWithModels();
        if (dataDir is null) return; // skip — model files not present

        var env = new AgentEnvironment { DataPath = dataDir };
        var config = new AgentConfig { EmbeddingModel = "multilingual-e5-base" };

        using var provider = new OnnxMultilingualE5EmbeddingProvider(env, config, NullLogger<OnnxMultilingualE5EmbeddingProvider>.Instance, autoDownload: false);

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

        var cosine = CosineSimilarity(passage, query);
        Assert.True(cosine < 0.999f, $"query/passage embeddings should differ; cosine was {cosine}");
    }

    private static string? LocateDataDirWithModels()
    {
        var envOverride = Environment.GetEnvironmentVariable("OPENAGENT_E5_DATA_DIR");
        if (!string.IsNullOrEmpty(envOverride)
            && File.Exists(Path.Combine(envOverride, "models", "multilingual-e5-base", "model.onnx")))
            return envOverride;

        var dir = AppContext.BaseDirectory;
        for (var depth = 0; depth < 8 && dir is not null; depth++)
        {
            var candidate = Path.Combine(dir, "models", "multilingual-e5-base");
            if (File.Exists(Path.Combine(candidate, "model.onnx"))
                && File.Exists(Path.Combine(candidate, "sentencepiece.bpe.model")))
                return dir; // the DATA dir, not the model dir
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
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"e5-test-{Guid.NewGuid()}");

        public TempDataDir() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            try { Directory.Delete(Path, true); } catch { }
        }
    }
}
