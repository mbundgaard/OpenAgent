using System.Text.Json;
using OpenAgent.Contracts;

namespace OpenAgent.Tests.Fakes;

/// <summary>No-op IConfigStore for tests that only need a provider's Configure() to succeed.</summary>
public sealed class NoopConfigStore : IConfigStore
{
    public JsonElement? Load(string key) => null;
    public void Save(string key, JsonElement config) { }
}
