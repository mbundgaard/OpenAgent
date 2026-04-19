using System.Text.Json;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Microsoft.ML.Tokenizers;
using OpenAgent.Contracts;

namespace OpenAgent.Embedding.Onnx;

/// <summary>
/// Local embedding provider using multilingual-e5-base via ONNX Runtime.
///
/// Pipeline: prefix with "query: " or "passage: ", tokenize via XLM-RoBERTa's Unigram
/// SentencePiece model, shift raw SentencePiece IDs by +1 to match HF's XLM-R ID space,
/// wrap with &lt;s&gt;/&lt;/s&gt;, truncate/pad to 512, run ONNX inference, mean-pool
/// over non-padding positions, L2 normalize, return float[768].
/// </summary>
public sealed class OnnxEmbeddingProvider : IEmbeddingProvider, IDisposable
{
    public const int MaxSequenceLength = 512;
    public const int EmbeddingDimensions = 768;

    // XLM-RoBERTa special-token ids. Confirmed in tokenizer.json added_tokens during the spike.
    private const int BosTokenId = 0;   // <s>
    private const int PadTokenId = 1;   // <pad>
    private const int EosTokenId = 2;   // </s>

    private readonly SentencePieceTokenizer _tokenizer;
    private readonly InferenceSession _session;
    private readonly string _inputIdsName;
    private readonly string _attentionMaskName;
    private readonly string _outputName;

    public string Key => "onnx";
    public int Dimensions => EmbeddingDimensions;

    /// <summary>
    /// Load the tokenizer and ONNX session from the given model directory. The directory
    /// must contain `model.onnx` and `sentencepiece.bpe.model`. Throws immediately if either
    /// file is missing rather than deferring the error until first use.
    /// </summary>
    public OnnxEmbeddingProvider(string modelDirectory)
    {
        if (!Directory.Exists(modelDirectory))
            throw new DirectoryNotFoundException($"ONNX embedding model directory not found: {modelDirectory}");

        var modelPath = Path.Combine(modelDirectory, "model.onnx");
        var tokenizerPath = Path.Combine(modelDirectory, "sentencepiece.bpe.model");

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

    /// <summary>
    /// Embed a single text. Applies the e5 prefix convention (query: / passage:) based on purpose.
    /// Returned vector is L2-normalized and has exactly <see cref="EmbeddingDimensions"/> components.
    /// </summary>
    public Task<float[]> GenerateEmbeddingAsync(string text, EmbeddingPurpose purpose, CancellationToken ct = default)
    {
        // e5 was trained to expect these prefixes; embedding quality drops sharply without them
        var prefixed = purpose == EmbeddingPurpose.Search ? "query: " + text : "passage: " + text;

        var rawIds = _tokenizer.EncodeToIds(prefixed);

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
        // Positions past bodyCount + 2 are left as zeros (PadTokenId = 1 in XLM-R, but the
        // attention_mask of 0 means those positions don't contribute to pooling. Using 0 in
        // input_ids is also safe — the model's pad token has id 1 per HF's vocab, but since
        // attention_mask zeros them out before softmax, the input id value at padded positions
        // is ignored in practice.)
        for (var i = bodyCount + 2; i < MaxSequenceLength; i++)
            inputIds[i] = PadTokenId;

        var embedding = RunInference(inputIds, attentionMask);
        return Task.FromResult(embedding);
    }

    private float[] RunInference(long[] inputIds, long[] attentionMask)
    {
        var inputIdsTensor = new DenseTensor<long>(inputIds, [1, MaxSequenceLength]);
        var attentionMaskTensor = new DenseTensor<long>(attentionMask, [1, MaxSequenceLength]);

        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor(_inputIdsName, inputIdsTensor),
            NamedOnnxValue.CreateFromTensor(_attentionMaskName, attentionMaskTensor),
        };

        using var results = _session.Run(inputs);
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
    /// Padding positions (mask = 0) are excluded. Result is a single vector of length <paramref name="hiddenSize"/>.
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
        _session.Dispose();
    }
}
