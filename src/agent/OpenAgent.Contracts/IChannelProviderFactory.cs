using OpenAgent.Models.Connections;
using OpenAgent.Models.Providers;

namespace OpenAgent.Contracts;

/// <summary>
/// Creates channel provider instances for a specific connection type.
/// Each channel project (Telegram, WhatsApp, etc.) registers one factory.
/// </summary>
public interface IChannelProviderFactory
{
    /// <summary>The connection type this factory handles (e.g. "telegram").</summary>
    string Type { get; }

    /// <summary>Human-readable name for this channel type.</summary>
    string DisplayName { get; }

    /// <summary>Configuration fields required to set up this channel type.</summary>
    IReadOnlyList<ProviderConfigField> ConfigFields { get; }

    /// <summary>Optional post-creation setup step (e.g. QR code scan for WhatsApp).</summary>
    ChannelSetupStep? SetupStep { get; }

    /// <summary>Creates a channel provider instance configured for the given connection.</summary>
    IChannelProvider Create(Connection connection);
}
