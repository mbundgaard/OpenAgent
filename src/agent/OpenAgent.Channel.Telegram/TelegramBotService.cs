using Microsoft.Extensions.Hosting;

namespace OpenAgent.Channel.Telegram;

/// <summary>
/// ASP.NET Core hosted service that starts and stops the Telegram channel provider.
/// </summary>
public sealed class TelegramBotService : IHostedService
{
    private readonly TelegramChannelProvider _channelProvider;

    public TelegramBotService(TelegramChannelProvider channelProvider)
    {
        _channelProvider = channelProvider;
    }

    public Task StartAsync(CancellationToken ct) => _channelProvider.StartAsync(ct);
    public Task StopAsync(CancellationToken ct) => _channelProvider.StopAsync(ct);
}
