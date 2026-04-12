using Microsoft.Extensions.Logging;
using OpenAgent.Contracts;

namespace OpenAgent.Channel.Telnyx;

/// <summary>
/// Channel provider that connects the agent to Telnyx voice calls.
/// This plan-1 implementation is a no-op skeleton — StartAsync/StopAsync
/// only log. Webhooks, call control, and media streaming arrive in later plans.
/// </summary>
public sealed class TelnyxChannelProvider : IChannelProvider
{
    private readonly TelnyxOptions _options;
    private readonly string _connectionId;
    private readonly ILogger<TelnyxChannelProvider> _logger;

    public TelnyxOptions Options => _options;
    public string ConnectionId => _connectionId;

    public TelnyxChannelProvider(
        TelnyxOptions options,
        string connectionId,
        ILogger<TelnyxChannelProvider> logger)
    {
        _options = options;
        _connectionId = connectionId;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken ct)
    {
        // Plan 1 scaffolding — no actual Telnyx traffic yet.
        _logger.LogInformation(
            "Telnyx channel {ConnectionId} started (phoneNumber={PhoneNumber}, allowedCount={AllowedCount})",
            _connectionId,
            _options.PhoneNumber ?? "<unset>",
            _options.AllowedNumbers.Count);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct)
    {
        _logger.LogInformation("Telnyx channel {ConnectionId} stopped", _connectionId);
        return Task.CompletedTask;
    }
}
