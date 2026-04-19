using System.Diagnostics;
using Microsoft.Extensions.Logging;
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
/// Missing model files are downloaded from HuggingFace on first use
/// (<c>https://huggingface.co/intfloat/{model}/</c>). Files land in
/// <c>{dataPath}/models/{model}/</c> and are kept across restarts.
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

    private const int BosTokenId = 0;   // <s>
    private const int PadTokenId = 1;   // <pad>
    private const int EosTokenId = 2;   // </s>

    private readonly string _modelDirectory;
    private readonly string _model;
    private readonly int _dimensions;
    private readonly ILogger<OnnxMultilingualE5EmbeddingProvider> _logger;
    private readonly bool _autoDownload;
    private readonly SemaphoreSlim _loadLock = new(1, 1);

    private SentencePieceTokenizer? _tokenizer;
    private InferenceSession? _session;
    private string? _inputIdsName;
    private string? _attentionMaskName;
    private string? _outputName;

    public string Key => ProviderKey;
    public string Model => _model;
    public int Dimensions => _dimensions;

    public OnnxMultilingualE5EmbeddingProvider(
        AgentEnvironment environment,
        AgentConfig agentConfig,
        ILogger<OnnxMultilingualE5EmbeddingProvider> logger,
        bool autoDownload = true)
    {
        _model = agentConfig.EmbeddingModel;
        if (!KnownModels.TryGetValue(_model, out _dimensions))
        {
            throw new InvalidOperationException(
                $"Unknown multilingual-e5 model: '{_model}'. Supported: {string.Join(", ", KnownModels.Keys)}");
        }
        _modelDirectory = Path.Combine(environment.DataPath, "models", _model);
        _logger = logger;
        _autoDownload = autoDownload;
    }

    public async Task<float[]> GenerateEmbeddingAsync(string text, EmbeddingPurpose purpose, CancellationToken ct = default)
    {
        await EnsureLoadedAsync(ct);

        // e5 was trained to expect these prefixes; embedding quality drops sharply without them
        var prefixed = purpose == EmbeddingPurpose.Search ? "query: " + text : "passage: " + text;

        var rawIds = _tokenizer!.EncodeToIds(prefixed);

        // Shift to XLM-R ID space. HF prepends <s> at 0, pushing every real token's id up by 1.
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
        for (var i = bodyCount + 2; i < MaxSequenceLength; i++)
            inputIds[i] = PadTokenId;

        return RunInference(inputIds, attentionMask);
    }

    private async Task EnsureLoadedAsync(CancellationToken ct)
    {
        if (_session is not null) return;
        await _loadLock.WaitAsync(ct);
        try
        {
            if (_session is not null) return;
            if (_autoDownload)
                await DownloadIfMissingAsync(ct);
            LoadLocalFiles();
        }
        finally
        {
            _loadLock.Release();
        }
    }

    private async Task DownloadIfMissingAsync(CancellationToken ct)
    {
        Directory.CreateDirectory(_modelDirectory);

        var sources = new (string FileName, string Url)[]
        {
            ("model.onnx",                $"https://huggingface.co/intfloat/{_model}/resolve/main/onnx/model.onnx"),
            ("sentencepiece.bpe.model",   $"https://huggingface.co/intfloat/{_model}/resolve/main/sentencepiece.bpe.model"),
        };

        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };

        foreach (var (fileName, url) in sources)
        {
            var path = Path.Combine(_modelDirectory, fileName);
            if (File.Exists(path) && new FileInfo(path).Length > 0)
                continue;

            _logger.LogInformation("Downloading {File} for {Model} from {Url}", fileName, _model, url);
            var sw = Stopwatch.StartNew();
            var tmpPath = path + ".tmp";
            try
            {
                using (var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct))
                {
                    response.EnsureSuccessStatusCode();
                    using var src = await response.Content.ReadAsStreamAsync(ct);
                    using var dst = File.Create(tmpPath);
                    await CopyWithProgressAsync(src, dst, fileName, ct);
                }
                File.Move(tmpPath, path, overwrite: true);
                sw.Stop();
                _logger.LogInformation(
                    "Downloaded {File} for {Model}: {MB:N1} MB in {Seconds:N1}s",
                    fileName, _model, new FileInfo(path).Length / 1024.0 / 1024.0, sw.Elapsed.TotalSeconds);
            }
            catch
            {
                try { File.Delete(tmpPath); } catch { /* best-effort cleanup */ }
                throw;
            }
        }
    }

    private async Task CopyWithProgressAsync(Stream src, Stream dst, string fileName, CancellationToken ct)
    {
        // Log every 100 MB so a multi-gigabyte download isn't silent.
        var buffer = new byte[81920];
        long total = 0;
        long lastLogged = 0;
        const long logEvery = 100L * 1024 * 1024;

        while (true)
        {
            var read = await src.ReadAsync(buffer, ct);
            if (read == 0) break;
            await dst.WriteAsync(buffer.AsMemory(0, read), ct);
            total += read;
            if (total - lastLogged >= logEvery)
            {
                _logger.LogInformation("Downloading {File}: {MB:N0} MB so far", fileName, total / 1024.0 / 1024.0);
                lastLogged = total;
            }
        }
    }

    private void LoadLocalFiles()
    {
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
        _loadLock.Dispose();
    }
}
