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

    /// <summary>Strongly-typed configuration for this connection. Exposed for tests that read back factory-parsed values.</summary>
    public TelnyxOptions Options => _options;

    /// <summary>Identifier of the owning connection row.</summary>
    public string ConnectionId => _connectionId;

    /// <summary>Creates a provider for the given connection. The factory is the only intended caller.</summary>
    public TelnyxChannelProvider(
        TelnyxOptions options,
        string connectionId,
        ILogger<TelnyxChannelProvider> logger)
    {
        _options = options;
        _connectionId = connectionId;
        _logger = logger;
    }

    /// <summary>No-op in plan 1 — logs the start event only. Real Telnyx wiring arrives in plan 2.</summary>
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

    /// <summary>No-op in plan 1 — logs the stop event only.</summary>
    public Task StopAsync(CancellationToken ct)
    {
        _logger.LogInformation("Telnyx channel {ConnectionId} stopped", _connectionId);
        return Task.CompletedTask;
    }
}
