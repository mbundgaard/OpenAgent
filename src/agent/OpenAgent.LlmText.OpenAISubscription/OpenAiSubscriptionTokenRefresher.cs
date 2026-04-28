using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenAgent.Contracts;

namespace OpenAgent.LlmText.OpenAISubscription;

/// <summary>
/// Background service that proactively refreshes the OpenAI Codex access + refresh
/// token pair every 24 hours. Lazy refresh in the provider already covers any token
/// that's actually expired, but a daily ping keeps the rotating refresh-token chain
/// alive even when the agent is otherwise idle for many days.
/// </summary>
public sealed class OpenAiSubscriptionTokenRefresher(
    IServiceProvider services,
    ILogger<OpenAiSubscriptionTokenRefresher> logger) : BackgroundService
{
    private static readonly TimeSpan Cadence = TimeSpan.FromHours(24);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Wait one full cadence before the first refresh — startup already saw a fresh
        // token (either just-loaded from disk or just-acquired via OAuth). Lazy refresh
        // on use catches any token that's actually expired in the meantime.
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(Cadence, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            try
            {
                var provider = (OpenAiSubscriptionTextProvider)services
                    .GetRequiredKeyedService<ILlmTextProvider>(OpenAiSubscriptionTextProvider.ProviderKey);
                await provider.RefreshNowAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "OpenAI subscription daily token refresh failed; will retry on next cycle.");
            }
        }
    }
}
