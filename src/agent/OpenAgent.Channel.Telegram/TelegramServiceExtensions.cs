using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenAgent.Contracts;

namespace OpenAgent.Channel.Telegram;

/// <summary>
/// Registers Telegram channel services: options, channel provider, and hosted service.
/// </summary>
public static class TelegramServiceExtensions
{
    /// <summary>
    /// Adds the Telegram channel provider, binds configuration from the "Telegram" section,
    /// and registers the hosted service that manages the bot lifecycle.
    /// </summary>
    public static IServiceCollection AddTelegramChannel(
        this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<TelegramOptions>(configuration.GetSection("Telegram"));
        services.AddSingleton<TelegramChannelProvider>();
        services.AddSingleton<IChannelProvider>(sp => sp.GetRequiredService<TelegramChannelProvider>());
        services.AddHostedService<TelegramBotService>();
        return services;
    }
}
