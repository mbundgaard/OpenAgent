namespace OpenAgent.Contracts;

/// <summary>
/// Whether the text being embedded is a chunk being stored or a query being searched.
/// Providers that distinguish the two (e.g. multilingual-e5 with its query:/passage: prefixes,
/// or Azure OpenAI's separate query/doc endpoints) should branch on this.
/// Providers that don't distinguish can ignore it.
/// </summary>
public enum EmbeddingPurpose
{
    /// <summary>The text is a chunk being indexed into storage.</summary>
    Indexing,
    /// <summary>The text is a search query.</summary>
    Search,
}

/// <summary>
/// Produces vector embeddings for text. Pluggable per-provider the same way ILlmTextProvider is;
/// the active provider is selected at runtime via AgentConfig.EmbeddingProvider and resolved
/// through a Func&lt;string, IEmbeddingProvider&gt; factory.
/// </summary>
public interface IEmbeddingProvider
{
    /// <summary>Unique provider key (e.g. "onnx", "azureOpenAi").</summary>
    string Key { get; }

    /// <summary>Embedding vector length. All vectors this provider returns have this dimensionality.</summary>
    int Dimensions { get; }

    /// <summary>
    /// Produce an embedding for the given text. The purpose hint lets providers apply any
    /// query/document-specific preprocessing (e.g. e5's "query: " / "passage: " prefix).
    /// </summary>
    Task<float[]> GenerateEmbeddingAsync(string text, EmbeddingPurpose purpose, CancellationToken ct = default);
}
