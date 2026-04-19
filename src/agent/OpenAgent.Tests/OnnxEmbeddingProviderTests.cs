using OpenAgent.Contracts;
using OpenAgent.Embedding.Onnx;

namespace OpenAgent.Tests;

public class OnnxEmbeddingProviderTests
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

        var pooled = OnnxEmbeddingProvider.MeanPool(tokenEmbeddings, mask, hiddenSize: 3);

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

        var pooled = OnnxEmbeddingProvider.MeanPool(tokenEmbeddings, mask, hiddenSize: 3);

        Assert.Equal([0f, 0f, 0f], pooled);
    }

    [Fact]
    public void L2Normalize_produces_unit_length_vector()
    {
        var input = new float[] { 3, 4, 0 }; // magnitude = 5
        var normalized = OnnxEmbeddingProvider.L2Normalize(input);

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
        var normalized = OnnxEmbeddingProvider.L2Normalize([0f, 0f, 0f]);

        foreach (var v in normalized)
            Assert.False(float.IsNaN(v), "zero input must not produce NaN");

        Assert.Equal([0f, 0f, 0f], normalized);
    }

    [Fact]
    public void Constructor_missing_directory_throws_DirectoryNotFoundException()
    {
        var nonexistent = Path.Combine(Path.GetTempPath(), $"nonexistent-{Guid.NewGuid()}");
        Assert.Throws<DirectoryNotFoundException>(() => new OnnxEmbeddingProvider(nonexistent));
    }

    [Fact]
    public void Constructor_missing_model_onnx_throws_FileNotFoundException()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"onnx-missing-{Guid.NewGuid()}");
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "sentencepiece.bpe.model"), "dummy");
            Assert.Throws<FileNotFoundException>(() => new OnnxEmbeddingProvider(dir));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    // Full end-to-end inference test. Skipped (via early return) when the real model files
    // are not present on the developer machine. To enable: download multilingual-e5-base into
    //   {repoRoot}/../models/multilingual-e5-base/  (model.onnx + sentencepiece.bpe.model)
    // or set the OPENAGENT_E5_MODEL_DIR environment variable to point at the directory.
    [Fact]
    public async Task GenerateEmbeddingAsync_returns_unit_vector_and_distinguishes_query_and_passage()
    {
        var modelDir = LocateModelDirectory();
        if (modelDir is null) return; // skip — model files not present

        using var provider = new OnnxEmbeddingProvider(modelDir);

        var passage = await provider.GenerateEmbeddingAsync("The quick brown fox jumps over the lazy dog.", EmbeddingPurpose.Indexing);
        var query = await provider.GenerateEmbeddingAsync("The quick brown fox jumps over the lazy dog.", EmbeddingPurpose.Search);

        Assert.Equal(OnnxEmbeddingProvider.EmbeddingDimensions, passage.Length);
        Assert.Equal(OnnxEmbeddingProvider.EmbeddingDimensions, query.Length);

        // Both should be L2-normalized unit vectors
        double magPassage = 0, magQuery = 0;
        for (var i = 0; i < passage.Length; i++)
        {
            magPassage += passage[i] * passage[i];
            magQuery += query[i] * query[i];
        }
        Assert.Equal(1.0, magPassage, precision: 3);
        Assert.Equal(1.0, magQuery, precision: 3);

        // Different prefixes must produce materially different embeddings
        var cosine = CosineSimilarity(passage, query);
        Assert.True(cosine < 0.999f, $"query/passage embeddings should differ; cosine was {cosine}");
    }

    private static string? LocateModelDirectory()
    {
        var envOverride = Environment.GetEnvironmentVariable("OPENAGENT_E5_MODEL_DIR");
        if (!string.IsNullOrEmpty(envOverride) && File.Exists(Path.Combine(envOverride, "model.onnx")))
            return envOverride;

        // Look upwards for a sibling `models/multilingual-e5-base/` directory
        var dir = AppContext.BaseDirectory;
        for (var depth = 0; depth < 8 && dir is not null; depth++)
        {
            var candidate = Path.Combine(dir, "models", "multilingual-e5-base");
            if (File.Exists(Path.Combine(candidate, "model.onnx"))
                && File.Exists(Path.Combine(candidate, "sentencepiece.bpe.model")))
                return candidate;
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
}
