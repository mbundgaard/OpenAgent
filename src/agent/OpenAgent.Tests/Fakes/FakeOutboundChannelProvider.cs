using OpenAgent.Contracts;

namespace OpenAgent.Tests.Fakes;

/// <summary>
/// Combined fake channel provider + outbound sender + connection manager that records every
/// message routed through <c>DeliveryRouter.DeliverAsync</c>'s channel-bound path. Lets tests
/// observe what is actually delivered to the user, as distinct from what ends up in conversation
/// history - the two are not the same thing, and that gap is exactly what the multi-round
/// heartbeat leak (see BackgroundAgentRunnerTests) exploited.
///
/// A conversation must have ChannelType, ConnectionId, and ChannelChatId all set for
/// DeliveryRouter to route to this fake instead of silently no-op'ing.
/// </summary>
public sealed class FakeOutboundChannelProvider : IChannelProvider, IOutboundSender, IConnectionManager
{
    /// <summary>Every (chatId, text) pair passed to SendMessageAsync, in call order.</summary>
    public List<(string ChatId, string Text)> SentMessages { get; } = [];

    public Task StartAsync(CancellationToken ct) => Task.CompletedTask;
    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;

    public Task SendMessageAsync(string chatId, string text, CancellationToken ct = default)
    {
        SentMessages.Add((chatId, text));
        return Task.CompletedTask;
    }

    // IConnectionManager — always reports this instance as the running provider for any
    // connection ID, which is all DeliveryRouter needs to route to it.
    public bool IsRunning(string connectionId) => true;
    public IChannelProvider? GetProvider(string connectionId) => this;
    public Task StartConnectionAsync(string connectionId, CancellationToken ct) => Task.CompletedTask;
    public Task StopConnectionAsync(string connectionId, CancellationToken ct) => Task.CompletedTask;
    public IEnumerable<(string ConnectionId, IChannelProvider Provider)> GetProviders() => [("fake", this)];
}
