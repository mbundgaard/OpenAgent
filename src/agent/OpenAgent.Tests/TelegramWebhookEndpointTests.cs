using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OpenAgent.Contracts;
using OpenAgent.Models.Connections;
using OpenAgent.Tests.Fakes;

namespace OpenAgent.Tests;

/// <summary>
/// Integration tests for the Telegram webhook endpoint HTTP behavior.
/// Validates secret-token auth and connection-based routing.
/// </summary>
public class TelegramWebhookEndpointTests : IClassFixture<WebApplicationFactory<Program>>
{
    private const string TestConnectionId = "test-telegram";
    private const string ConversationId = "webhook-conv-1";

    private readonly WebApplicationFactory<Program> _factory;

    public TelegramWebhookEndpointTests(WebApplicationFactory<Program> factory)
    {
        TestSetup.EnsureConfigSeeded();
        _factory = factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                // Replace ILlmTextProvider with a fake
                services.RemoveAll(typeof(ILlmTextProvider));
                services.AddSingleton<ILlmTextProvider>(new FakeTelegramTextProvider("ok"));
            });
        });
    }

    /// <summary>Creates a test connection in the store and starts it via the ConnectionManager.</summary>
    private async Task<string> SetupConnectionAsync()
    {
        var store = _factory.Services.GetRequiredService<IConnectionStore>();
        var connectionManager = _factory.Services.GetRequiredService<IConnectionManager>();

        var telegramConfig = JsonSerializer.SerializeToElement(new
        {
            botToken = "123456:ABC-DEF1234ghIkl-zyx57W2v1u123ew11",
            mode = "Webhook",
            webhookUrl = $"https://example.com/api/connections/{TestConnectionId}/webhook/telegram",
            webhookSecret = "test-secret"
        });

        var connection = new Connection
        {
            Id = TestConnectionId,
            Name = "Test Bot",
            Type = "telegram",
            Enabled = true,
            ConversationId = ConversationId,
            Config = telegramConfig,
        };

        store.Save(connection);
        // Note: we don't call StartConnectionAsync because it would try to call the real Telegram API
        return "test-secret";
    }

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
    public async Task Webhook_NoRunningConnection_Returns404()
    {
        var client = _factory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/connections/{TestConnectionId}/webhook/telegram")
        {
            Content = CreateValidUpdateJson()
        };
        request.Headers.Add("X-Telegram-Bot-Api-Secret-Token", "test-secret");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Webhook_IsAnonymous_NoApiKeyNeeded()
    {
        var client = _factory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/connections/nonexistent/webhook/telegram")
        {
            Content = CreateValidUpdateJson()
        };
        // No X-Api-Key header — endpoint is AllowAnonymous

        var response = await client.SendAsync(request);

        // Should be 404 (no connection), not 401 (auth required)
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
