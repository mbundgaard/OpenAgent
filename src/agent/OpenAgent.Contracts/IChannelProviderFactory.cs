using OpenAgent.Models.Connections;

namespace OpenAgent.Contracts;

/// <summary>
/// Creates channel provider instances for a specific connection type.
/// Each channel project (Telegram, WhatsApp, etc.) registers one factory.
/// </summary>
public interface IChannelProviderFactory
{
    /// <summary>The connection type this factory handles (e.g. "telegram").</summary>
    string Type { get; }

    /// <summary>Creates a channel provider instance configured for the given connection.</summary>
    IChannelProvider Create(Connection connection);
}
