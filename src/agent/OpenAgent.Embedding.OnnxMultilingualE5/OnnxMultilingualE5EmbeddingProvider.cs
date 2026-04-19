using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Microsoft.ML.Tokenizers;
using OpenAgent.Contracts;
using OpenAgent.Models.Configs;

namespace OpenAgent.Embedding.OnnxMultilingualE5;

/// <summary>
/// Local embedding provider for the <c>intfloat/multilingual-e5-{small,base,large}</c> family
/// running on ONNX Runtime. All three variants share the same XLM-RoBERTa Unigram SentencePiece
/// tokenizer and special-token layout — they only differ in hidden size. The specific model to
/// load is chosen via <see cref="AgentConfig.EmbeddingModel"/>.
///
/// Pipeline: prefix with "query: " or "passage: ", tokenize via XLM-RoBERTa's Unigram
/// SentencePiece model, shift raw SentencePiece IDs by +1 to match HF's XLM-R ID space,
/// wrap with &lt;s&gt;/&lt;/s&gt;, truncate/pad to 512, run ONNX inference, mean-pool
/// over non-padding positions, L2 normalize.
/// </summary>
public sealed class OnnxMultilingualE5EmbeddingProvider : IEmbeddingProvider, IDisposable
{
    public const string ProviderKey = "multilingual-e5";
    public const int MaxSequenceLength = 512;

    /// <summary>
    /// Known e5 variants and their embedding dimensions. Used to report <see cref="Dimensions"/>
    /// without loading the ONNX session, and to validate <see cref="AgentConfig.EmbeddingModel"/>
    /// at construction time.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, int> KnownModels = new Dictionary<string, int>
    {
        ["multilingual-e5-small"] = 384,
        ["multilingual-e5-base"] = 768,
        ["multilingual-e5-large"] = 1024,
    };

    // XLM-RoBERTa special-token ids. Confirmed in tokenizer.json added_tokens during the spike.
    private const int BosTokenId = 0;   // <s>
    private const int PadTokenId = 1;   // <pad>
    private const int EosTokenId = 2;   // </s>

    private readonly string _modelDirectory;
    private readonly string _model;
    private readonly int _dimensions;

    private SentencePieceTokenizer? _tokenizer;
    private InferenceSession? _session;
    private string? _inputIdsName;
    private string? _attentionMaskName;
    private string? _outputName;

    public string Key => ProviderKey;
    public string Model => _model;
    public int Dimensions => _dimensions;

    /// <summary>
    /// Resolves the model directory from <see cref="AgentEnvironment"/> + <see cref="AgentConfig.EmbeddingModel"/>
    /// and stores it. Files are loaded lazily on first use so that a misconfigured or
    /// not-yet-downloaded model doesn't crash app startup — the error surfaces on the first
    /// embedding call, where it's caught by the indexing loop's per-file try/catch.
    /// </summary>
    public OnnxMultilingualE5EmbeddingProvider(AgentEnvironment environment, AgentConfig agentConfig)
    {
        _model = agentConfig.EmbeddingModel;
        if (!KnownModels.TryGetValue(_model, out _dimensions))
        {
            throw new InvalidOperationException(
                $"Unknown multilingual-e5 model: '{_model}'. Supported: {string.Join(", ", KnownModels.Keys)}");
        }
        _modelDirectory = Path.Combine(environment.DataPath, "models", _model);
    }

    /// <summary>
    /// Embed a single text. Applies the e5 prefix convention (query: / passage:) based on purpose.
    /// Returned vector is L2-normalized and has exactly <see cref="Dimensions"/> components.
    /// </summary>
    public Task<float[]> GenerateEmbeddingAsync(string text, EmbeddingPurpose purpose, CancellationToken ct = default)
    {
        EnsureLoaded();

        // e5 was trained to expect these prefixes; embedding quality drops sharply without them
        var prefixed = purpose == EmbeddingPurpose.Search ? "query: " + text : "passage: " + text;

        var rawIds = _tokenizer!.EncodeToIds(prefixed);

        // Shift to XLM-R ID space. HF prepends <s> at 0, pushing every real token's id up by 1.
        // Truncate to leave room for <s> + </s>.
        var maxBodyTokens = MaxSequenceLength - 2;
        var bodyCount = Math.Min(rawIds.Count, maxBodyTokens);

        var inputIds = new long[MaxSequenceLength];
        var attentionMask = new long[MaxSequenceLength];

        inputIds[0] = BosTokenId;
        attentionMask[0] = 1;
        for (var i = 0; i < bodyCount; i++)
        {
            inputIds[i + 1] = rawIds[i] + 1;
            attentionMask[i + 1] = 1;
        }
        inputIds[bodyCount + 1] = EosTokenId;
        attentionMask[bodyCount + 1] = 1;
        // Positions past bodyCount + 2 are zero-filled; attention_mask of 0 ensures they don't
        // contribute to pooling. We then overwrite with PadTokenId for completeness.
        for (var i = bodyCount + 2; i < MaxSequenceLength; i++)
            inputIds[i] = PadTokenId;

        var embedding = RunInference(inputIds, attentionMask);
        return Task.FromResult(embedding);
    }

    private void EnsureLoaded()
    {
        if (_session is not null) return;

        if (!Directory.Exists(_modelDirectory))
            throw new DirectoryNotFoundException($"e5 model directory not found: {_modelDirectory}");

        var modelPath = Path.Combine(_modelDirectory, "model.onnx");
        var tokenizerPath = Path.Combine(_modelDirectory, "sentencepiece.bpe.model");

        if (!File.Exists(modelPath))
            throw new FileNotFoundException($"ONNX model file missing: {modelPath}");
        if (!File.Exists(tokenizerPath))
            throw new FileNotFoundException($"SentencePiece model file missing: {tokenizerPath}");

        using (var fs = File.OpenRead(tokenizerPath))
        {
            _tokenizer = SentencePieceTokenizer.Create(fs, addBeginningOfSentence: false, addEndOfSentence: false, specialTokens: null);
        }

        _session = new InferenceSession(modelPath);
        _inputIdsName = _session.InputMetadata.ContainsKey("input_ids") ? "input_ids" : _session.InputMetadata.Keys.First();
        _attentionMaskName = _session.InputMetadata.ContainsKey("attention_mask") ? "attention_mask" : _session.InputMetadata.Keys.Skip(1).First();
        _outputName = _session.OutputMetadata.Keys.First();
    }

    private float[] RunInference(long[] inputIds, long[] attentionMask)
    {
        var inputIdsTensor = new DenseTensor<long>(inputIds, [1, MaxSequenceLength]);
        var attentionMaskTensor = new DenseTensor<long>(attentionMask, [1, MaxSequenceLength]);

        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor(_inputIdsName!, inputIdsTensor),
            NamedOnnxValue.CreateFromTensor(_attentionMaskName!, attentionMaskTensor),
        };

        using var results = _session!.Run(inputs);
        var output = results.First(r => r.Name == _outputName).AsTensor<float>();

        // last_hidden_state shape: [1, seq, hidden]. Mean-pool over seq dimension using the mask.
        var seqLen = output.Dimensions[1];
        var hiddenSize = output.Dimensions[2];
        var tokenEmbeddings = new float[seqLen * hiddenSize];
        var idx = 0;
        for (var i = 0; i < seqLen; i++)
            for (var j = 0; j < hiddenSize; j++)
                tokenEmbeddings[idx++] = output[0, i, j];

        var pooled = MeanPool(tokenEmbeddings, attentionMask, hiddenSize);
        return L2Normalize(pooled);
    }

    /// <summary>
    /// Mean-pool a [seq, hidden] flattened tensor weighted by a binary attention mask.
    /// Padding positions (mask = 0) are excluded.
    /// </summary>
    internal static float[] MeanPool(float[] tokenEmbeddings, long[] attentionMask, int hiddenSize)
    {
        var pooled = new float[hiddenSize];
        long realCount = 0;

        for (var i = 0; i < attentionMask.Length; i++)
        {
            if (attentionMask[i] == 0) continue;
            realCount++;
            var rowStart = i * hiddenSize;
            for (var j = 0; j < hiddenSize; j++)
                pooled[j] += tokenEmbeddings[rowStart + j];
        }

        if (realCount == 0) return pooled;
        for (var j = 0; j < hiddenSize; j++)
            pooled[j] /= realCount;
        return pooled;
    }

    /// <summary>
    /// L2-normalize a vector. Zero-magnitude input returns the zero vector rather than producing NaN.
    /// </summary>
    internal static float[] L2Normalize(float[] vector)
    {
        double sumSquares = 0;
        for (var i = 0; i < vector.Length; i++)
            sumSquares += vector[i] * vector[i];

        if (sumSquares == 0) return vector;

        var norm = Math.Sqrt(sumSquares);
        var result = new float[vector.Length];
        for (var i = 0; i < vector.Length; i++)
            result[i] = (float)(vector[i] / norm);
        return result;
    }

    public void Dispose()
    {
        _session?.Dispose();
    }
}
