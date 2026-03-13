namespace OpenAgent.Contracts;

/// <summary>
/// Inbound channel that receives messages from external platforms (Telegram, Discord, etc.)
/// and forwards them through the agent pipeline.
/// </summary>
public interface IChannelProvider
{
    /// <summary>Start listening for inbound messages (polling, subscriptions, etc.).</summary>
    Task StartAsync(CancellationToken ct);

    /// <summary>Stop listening and clean up resources.</summary>
    Task StopAsync(CancellationToken ct);
}
