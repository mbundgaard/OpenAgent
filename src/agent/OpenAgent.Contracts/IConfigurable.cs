using OpenAgent.Models.Providers;
using System.Text.Json;

namespace OpenAgent.Contracts;

public interface IConfigurable
{
    IReadOnlyList<ProviderConfigField> ConfigFields { get; }
    void Configure(JsonElement configuration);
}