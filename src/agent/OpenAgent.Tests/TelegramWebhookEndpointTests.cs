using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenAgent.Channel.Telegram;
using OpenAgent.Contracts;
using OpenAgent.Tests.Fakes;
using Telegram.Bot;

namespace OpenAgent.Tests;

/// <summary>
/// Integration tests for the Telegram webhook endpoint HTTP behavior.
/// Validates secret-token auth, anonymous access, and successful update processing.
/// </summary>
public class TelegramWebhookEndpointTests : IClassFixture<WebApplicationFactory<Program>>
{
    private const string WebhookSecret = "test-secret";
    private const string FakeBotToken = "123456:ABC-DEF1234ghIkl-zyx57W2v1u123ew11";

    private readonly WebApplicationFactory<Program> _factory;

    public TelegramWebhookEndpointTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory.WithWebHostBuilder(builder =>
        {
            // Configure Telegram settings
            builder.UseSetting("Telegram:BotToken", FakeBotToken);
            builder.UseSetting("Telegram:Mode", "Webhook");
            builder.UseSetting("Telegram:WebhookUrl", "https://example.com/api/telegram/webhook");
            builder.UseSetting("Telegram:WebhookSecret", WebhookSecret);
            builder.UseSetting("Telegram:AllowedUserIds:0", "42");

            builder.ConfigureServices(services =>
            {
                // Replace ILlmTextProvider with a fake
                var textDescriptor = services.SingleOrDefault(d => d.ServiceType == typeof(ILlmTextProvider));
                if (textDescriptor is not null) services.Remove(textDescriptor);
                services.AddSingleton<ILlmTextProvider>(new FakeTelegramTextProvider("ok"));

                // Remove the real hosted service to prevent SetWebhook call to Telegram API
                var hostedDescriptor = services.SingleOrDefault(
                    d => d.ImplementationType == typeof(TelegramBotService));
                if (hostedDescriptor is not null) services.Remove(hostedDescriptor);
            });
        });
    }

    /// <summary>
    /// Initializes the TelegramChannelProvider's private fields via reflection
    /// so the webhook endpoint can function without calling the real Telegram API.
    /// </summary>
    private void InitializeProvider(IServiceProvider services)
    {
        var provider = services.GetRequiredService<TelegramChannelProvider>();
        var providerType = typeof(TelegramChannelProvider);

        // Set _botClient — constructor only stores the token, no API call
        var botClientField = providerType.GetField("_botClient", BindingFlags.NonPublic | BindingFlags.Instance)!;
        botClientField.SetValue(provider, new TelegramBotClient(FakeBotToken));

        // Set _webhookSecret
        var secretField = providerType.GetField("_webhookSecret", BindingFlags.NonPublic | BindingFlags.Instance)!;
        secretField.SetValue(provider, WebhookSecret);

        // Set _handler — needs store, text provider, options, and logger
        var store = services.GetRequiredService<IConversationStore>();
        var textProvider = services.GetRequiredService<ILlmTextProvider>();
        var options = new TelegramOptions
        {
            BotToken = FakeBotToken,
            Mode = "Webhook",
            WebhookUrl = "https://example.com/api/telegram/webhook",
            WebhookSecret = WebhookSecret,
            AllowedUserIds = [42]
        };
        var handlerLogger = services.GetRequiredService<Microsoft.Extensions.Logging.ILogger<TelegramMessageHandler>>();

        var handler = new TelegramMessageHandler(store, textProvider, options, handlerLogger);
        var handlerField = providerType.GetField("_handler", BindingFlags.NonPublic | BindingFlags.Instance)!;
        handlerField.SetValue(provider, handler);
    }

    /// <summary>Creates an HTTP client with the provider manually initialized.</summary>
    private HttpClient CreateInitializedClient()
    {
        var client = _factory.CreateClient();

        // Initialize the provider via the test server's service provider
        InitializeProvider(_factory.Services);

        return client;
    }

    /// <summary>Builds a minimal valid Telegram Update JSON payload.</summary>
    private static StringContent CreateValidUpdateJson()
    {
        var update = new
        {
            update_id = 1,
            message = new
            {
                message_id = 1,
                date = 0,
                chat = new { id = 42, type = "private" },
                from = new { id = 42, is_bot = false, first_name = "Test" },
                text = "hello"
            }
        };

        var json = JsonSerializer.Serialize(update);
        return new StringContent(json, Encoding.UTF8, "application/json");
    }

    [Fact]
    public async Task Webhook_ValidUpdate_Returns200()
    {
        // Arrange
        var client = CreateInitializedClient();
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/telegram/webhook")
        {
            Content = CreateValidUpdateJson()
        };
        request.Headers.Add("X-Telegram-Bot-Api-Secret-Token", WebhookSecret);

        // Act
        var response = await client.SendAsync(request);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Webhook_MissingSecret_Returns401()
    {
        // Arrange
        var client = CreateInitializedClient();
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/telegram/webhook")
        {
            Content = CreateValidUpdateJson()
        };
        // No X-Telegram-Bot-Api-Secret-Token header

        // Act
        var response = await client.SendAsync(request);

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Webhook_WrongSecret_Returns401()
    {
        // Arrange
        var client = CreateInitializedClient();
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/telegram/webhook")
        {
            Content = CreateValidUpdateJson()
        };
        request.Headers.Add("X-Telegram-Bot-Api-Secret-Token", "wrong-secret");

        // Act
        var response = await client.SendAsync(request);

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Webhook_IsAnonymous_NoApiKeyNeeded()
    {
        // Arrange — no X-Api-Key header, but correct webhook secret
        var client = CreateInitializedClient();
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/telegram/webhook")
        {
            Content = CreateValidUpdateJson()
        };
        request.Headers.Add("X-Telegram-Bot-Api-Secret-Token", WebhookSecret);
        // Explicitly NOT adding X-Api-Key

        // Act
        var response = await client.SendAsync(request);

        // Assert — should be 200, not 401 (endpoint is AllowAnonymous)
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
