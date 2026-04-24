using OpenAgent.Contracts;

namespace OpenAgent.Tests.Fakes;

/// <summary>
/// Deterministic fake IEmbeddingProvider for tests. By default returns a hash-derived vector
/// so distinct inputs map to distinct vectors; callers can preload a specific input→vector
/// mapping via <see cref="Set"/> when a test needs precise control.
/// </summary>
public sealed class FakeEmbeddingProvider : IEmbeddingProvider
{
    private readonly Dictionary<string, float[]> _overrides = new();

    public FakeEmbeddingProvider(string key = "fake", string model = "fake-model", int dimensions = 4)
    {
        Key = key;
        Model = model;
        Dimensions = dimensions;
    }

    public string Key { get; }
    public string Model { get; }
    public int Dimensions { get; }

    public void Set(string text, EmbeddingPurpose purpose, float[] vector)
    {
        _overrides[MakeKey(text, purpose)] = vector;
    }

    public Task<float[]> GenerateEmbeddingAsync(string text, EmbeddingPurpose purpose, CancellationToken ct = default)
    {
        if (_overrides.TryGetValue(MakeKey(text, purpose), out var preset))
            return Task.FromResult(preset);

        var hash = text.GetHashCode();
        var rng = new Random(hash);
        var vector = new float[Dimensions];
        for (var i = 0; i < Dimensions; i++)
            vector[i] = (float)(rng.NextDouble() * 2 - 1);
        return Task.FromResult(vector);
    }

    private static string MakeKey(string text, EmbeddingPurpose purpose) => $"{(int)purpose}|{text}";
}
