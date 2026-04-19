using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Microsoft.ML.Tokenizers;
using OpenAgent.Contracts;
using OpenAgent.Models.Configs;

namespace OpenAgent.Embedding.OnnxBge;

/// <summary>
/// Local English-only embedding provider for the BAAI/bge-{small,base,large}-en-v1.5 family
/// running on ONNX Runtime. All three variants share the same BERT WordPiece tokenizer and
/// special-token layout — they only differ in hidden size. The specific model to load is
/// chosen via <see cref="AgentConfig.EmbeddingModel"/>.
///
/// <para>Model sizes (model.onnx on disk ≈ resident RAM when loaded; add ~150–250 MB for
/// ONNX Runtime overhead and per-request tensors):</para>
/// <list type="bullet">
///   <item><description><b>bge-small-en-v1.5</b> — 384 dims, ~130 MB disk, ~300 MB RAM. Very small; great for English-only low-footprint deployments.</description></item>
///   <item><description><b>bge-base-en-v1.5</b> — 768 dims, ~440 MB disk, ~600 MB RAM. Strong English baseline, comparable to e5-base in quality for English text.</description></item>
///   <item><description><b>bge-large-en-v1.5</b> — 1024 dims, ~1.3 GB disk, ~1.5 GB RAM. Top English retrieval quality on MTEB for its size class.</description></item>
/// </list>
///
/// Missing model files are downloaded from HuggingFace on first use
/// (<c>https://huggingface.co/BAAI/{model}/</c>). Files land in
/// <c>{dataPath}/models/{model}/</c> and are kept across restarts.
///
/// Pipeline: prepend the BGE query instruction when <see cref="EmbeddingPurpose.Search"/>
/// (passages get no prefix — BGE v1.5 asymmetric retrieval), tokenize via BERT WordPiece,
/// wrap with [CLS]/[SEP], pad to 512, run ONNX inference with input_ids + attention_mask
/// (+ token_type_ids=0 when the session expects it), take the [CLS] token's hidden state
/// (position 0 of last_hidden_state), L2 normalize.
///
/// Distinct from the multilingual-e5 provider in three ways:
/// - BERT WordPiece (vocab.txt) vs XLM-R Unigram SentencePiece (sentencepiece.bpe.model)
/// - No ID offset: HF-compatible ids come straight from BertTokenizer
/// - CLS pooling (position 0 only) vs mean pooling over non-padding positions
/// </summary>
public sealed class OnnxBgeEmbeddingProvider : IEmbeddingProvider, IDisposable
{
    public const string ProviderKey = "bge";
    public const int MaxSequenceLength = 512;

    private const string QueryInstruction = "Represent this sentence for searching relevant passages: ";

    public static readonly IReadOnlyDictionary<string, int> KnownModels = new Dictionary<string, int>
    {
        ["bge-small-en-v1.5"] = 384,
        ["bge-base-en-v1.5"] = 768,
        ["bge-large-en-v1.5"] = 1024,
    };

    // Standard BERT-uncased special-token ids (bge-*-en-v1.5 uses the bert-base vocab layout).
    private const int PadTokenId = 0;
    private const int ClsTokenId = 101;
    private const int SepTokenId = 102;

    private readonly string _modelDirectory;
    private readonly string _model;
    private readonly int _dimensions;
    private readonly ILogger<OnnxBgeEmbeddingProvider> _logger;
    private readonly bool _autoDownload;
    private readonly SemaphoreSlim _loadLock = new(1, 1);

    private BertTokenizer? _tokenizer;
    private InferenceSession? _session;
    private string? _inputIdsName;
    private string? _attentionMaskName;
    private string? _tokenTypeIdsName;
    private string? _outputName;

    public string Key => ProviderKey;
    public string Model => _model;
    public int Dimensions => _dimensions;

    public OnnxBgeEmbeddingProvider(
        AgentEnvironment environment,
        AgentConfig agentConfig,
        ILogger<OnnxBgeEmbeddingProvider> logger,
        bool autoDownload = true)
    {
        _model = agentConfig.EmbeddingModel;
        if (!KnownModels.TryGetValue(_model, out _dimensions))
        {
            throw new InvalidOperationException(
                $"Unknown BGE model: '{_model}'. Supported: {string.Join(", ", KnownModels.Keys)}");
        }
        _modelDirectory = Path.Combine(environment.DataPath, "models", _model);
        _logger = logger;
        _autoDownload = autoDownload;
    }

    public async Task<float[]> GenerateEmbeddingAsync(string text, EmbeddingPurpose purpose, CancellationToken ct = default)
    {
        await EnsureLoadedAsync(ct);

        var prefixed = purpose == EmbeddingPurpose.Search ? QueryInstruction + text : text;

        // Bare subword ids — we add [CLS]/[SEP] ourselves so truncation/padding lives in one place.
        var rawIds = _tokenizer!.EncodeToIds(prefixed, addSpecialTokens: false);

        var maxBodyTokens = MaxSequenceLength - 2;
        var bodyCount = Math.Min(rawIds.Count, maxBodyTokens);

        var inputIds = new long[MaxSequenceLength];
        var attentionMask = new long[MaxSequenceLength];
        var tokenTypeIds = new long[MaxSequenceLength];

        inputIds[0] = ClsTokenId;
        attentionMask[0] = 1;
        for (var i = 0; i < bodyCount; i++)
        {
            inputIds[i + 1] = rawIds[i];
            attentionMask[i + 1] = 1;
        }
        inputIds[bodyCount + 1] = SepTokenId;
        attentionMask[bodyCount + 1] = 1;
        // Positions past bodyCount + 2 stay at 0 = [PAD] with attention_mask = 0.

        return RunInference(inputIds, attentionMask, tokenTypeIds);
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
            ("model.onnx", $"https://huggingface.co/BAAI/{_model}/resolve/main/onnx/model.onnx"),
            ("vocab.txt",  $"https://huggingface.co/BAAI/{_model}/resolve/main/vocab.txt"),
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
            throw new DirectoryNotFoundException($"BGE model directory not found: {_modelDirectory}");

        var modelPath = Path.Combine(_modelDirectory, "model.onnx");
        var vocabPath = Path.Combine(_modelDirectory, "vocab.txt");

        if (!File.Exists(modelPath))
            throw new FileNotFoundException($"ONNX model file missing: {modelPath}");
        if (!File.Exists(vocabPath))
            throw new FileNotFoundException($"vocab.txt missing: {vocabPath}");

        using (var fs = File.OpenRead(vocabPath))
        {
            _tokenizer = BertTokenizer.Create(fs, new BertOptions { LowerCaseBeforeTokenization = true });
        }

        _session = new InferenceSession(modelPath);
        _inputIdsName = _session.InputMetadata.ContainsKey("input_ids") ? "input_ids" : _session.InputMetadata.Keys.First();
        _attentionMaskName = _session.InputMetadata.ContainsKey("attention_mask") ? "attention_mask" : _session.InputMetadata.Keys.ElementAtOrDefault(1);
        _tokenTypeIdsName = _session.InputMetadata.ContainsKey("token_type_ids") ? "token_type_ids" : null;
        _outputName = _session.OutputMetadata.Keys.First();
    }

    private float[] RunInference(long[] inputIds, long[] attentionMask, long[] tokenTypeIds)
    {
        var inputIdsTensor = new DenseTensor<long>(inputIds, [1, MaxSequenceLength]);
        var attentionMaskTensor = new DenseTensor<long>(attentionMask, [1, MaxSequenceLength]);

        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor(_inputIdsName!, inputIdsTensor),
            NamedOnnxValue.CreateFromTensor(_attentionMaskName!, attentionMaskTensor),
        };
        if (_tokenTypeIdsName is not null)
        {
            var tokenTypeIdsTensor = new DenseTensor<long>(tokenTypeIds, [1, MaxSequenceLength]);
            inputs.Add(NamedOnnxValue.CreateFromTensor(_tokenTypeIdsName, tokenTypeIdsTensor));
        }

        using var results = _session!.Run(inputs);
        var output = results.First(r => r.Name == _outputName).AsTensor<float>();

        // CLS pooling: position 0 of last_hidden_state [1, seq, hidden].
        var hiddenSize = output.Dimensions[2];
        var cls = new float[hiddenSize];
        for (var j = 0; j < hiddenSize; j++)
            cls[j] = output[0, 0, j];

        return L2Normalize(cls);
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
