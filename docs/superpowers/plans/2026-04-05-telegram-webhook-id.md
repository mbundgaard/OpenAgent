# Telegram Webhook ID — Auto-Generated Webhook URLs

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace user-provided webhook URLs with auto-generated ones. The user provides a base URL (e.g. `https://openagent-comput.azurewebsites.net`), the Telegram provider generates a GUID (`webhookId`) on first start, saves it to the connection config, and computes the full webhook URL. The endpoint route changes from `/api/connections/{connectionId}/webhook/telegram` to `/api/webhook/telegram/{webhookId}`, removing the chicken-and-egg problem.

**Architecture:** On `StartAsync`, the provider checks if `webhookId` exists in config. If not, it generates one (`Guid.NewGuid().ToString("N")`), writes it back to the connection via `IConnectionStore.Save`, and computes the webhook URL as `{baseUrl}/api/webhook/telegram/{webhookId}`. The webhook endpoint looks up the running provider by scanning all running Telegram providers for a matching `webhookId`. The `webhookUrl` config field is removed from user input and replaced with `baseUrl`.

**Tech Stack:** .NET 10, ASP.NET Core, xUnit

---

## File Structure

| Action | Path | Responsibility |
|--------|------|----------------|
| Modify | `src/agent/OpenAgent.Channel.Telegram/TelegramOptions.cs` | Replace `WebhookUrl` with `BaseUrl`, add `WebhookId` |
| Modify | `src/agent/OpenAgent.Channel.Telegram/TelegramChannelProviderFactory.cs` | Parse `baseUrl` instead of `webhookUrl`, parse `webhookId` from config |
| Modify | `src/agent/OpenAgent.Channel.Telegram/TelegramChannelProvider.cs` | Generate webhookId on start, save to connection, compute webhook URL |
| Modify | `src/agent/OpenAgent.Channel.Telegram/TelegramWebhookEndpoints.cs` | New route, lookup by webhookId |
| Modify | `src/agent/OpenAgent.Contracts/IConnectionManager.cs` | Add `GetProviders()` method |
| Modify | `src/agent/OpenAgent/ConnectionManager.cs` | Implement `GetProviders()` |
| Modify | `src/agent/OpenAgent.Tests/TelegramWebhookEndpointTests.cs` | Update for new route and config |

---

### Task 1: Update TelegramOptions

**Files:**
- Modify: `src/agent/OpenAgent.Channel.Telegram/TelegramOptions.cs`

- [ ] **Step 1: Replace WebhookUrl with BaseUrl and add WebhookId**

Change:

```csharp
/// <summary>
/// Public HTTPS URL for Telegram to send webhook updates to. Required when Mode is "Webhook".
/// </summary>
public string? WebhookUrl { get; set; }
```

to:

```csharp
/// <summary>
/// Base URL of the OpenAgent instance (e.g. "https://openagent-comput.azurewebsites.net").
/// Required when Mode is "Webhook". The full webhook URL is computed automatically.
/// </summary>
public string? BaseUrl { get; set; }

/// <summary>
/// Auto-generated GUID identifying this connection's webhook endpoint.
/// Generated on first start and persisted in the connection config.
/// </summary>
public string? WebhookId { get; set; }
```

- [ ] **Step 2: Build**

Run: `cd src/agent && dotnet build OpenAgent.Channel.Telegram`
Expected: Compilation errors in provider and factory — expected, fixed in next tasks.

---

### Task 2: Update Factory Config Parsing

**Files:**
- Modify: `src/agent/OpenAgent.Channel.Telegram/TelegramChannelProviderFactory.cs`

- [ ] **Step 1: Update ConfigFields**

Replace the `webhookUrl` config field with `baseUrl`. Change line 29 from:

```csharp
new() { Key = "webhookUrl", Label = "Webhook URL", Type = "String" },
```

to:

```csharp
new() { Key = "baseUrl", Label = "Base URL", Type = "String" },
```

- [ ] **Step 2: Update config parsing in Create()**

Replace the `webhookUrl` parsing block (lines 83-84):

```csharp
if (connection.Config.TryGetProperty("webhookUrl", out var urlEl) && urlEl.ValueKind == JsonValueKind.String)
    options.WebhookUrl = urlEl.GetString();
```

with:

```csharp
if (connection.Config.TryGetProperty("baseUrl", out var baseUrlEl) && baseUrlEl.ValueKind == JsonValueKind.String)
    options.BaseUrl = baseUrlEl.GetString();

if (connection.Config.TryGetProperty("webhookId", out var webhookIdEl) && webhookIdEl.ValueKind == JsonValueKind.String)
    options.WebhookId = webhookIdEl.GetString();
```

- [ ] **Step 3: Build**

Run: `cd src/agent && dotnet build OpenAgent.Channel.Telegram`
Expected: Compilation errors in TelegramChannelProvider — fixed in next task.

---

### Task 3: Update Provider — Generate webhookId and Compute URL

**Files:**
- Modify: `src/agent/OpenAgent.Channel.Telegram/TelegramChannelProvider.cs`

- [ ] **Step 1: Update StartAsync webhook mode**

Replace the webhook block in `StartAsync` (lines 77-92):

```csharp
if (isWebhook)
{
    if (string.IsNullOrEmpty(_options.WebhookUrl))
        throw new InvalidOperationException(
            "Telegram WebhookUrl is required when Mode is 'Webhook'.");

    // Generate webhook secret if not configured
    _webhookSecret = _options.WebhookSecret ?? Guid.NewGuid().ToString("N");

    // Register webhook with Telegram
    await _botClient.SetWebhook(
        _options.WebhookUrl,
        secretToken: _webhookSecret,
        cancellationToken: ct);

    _logger.LogInformation("Telegram: webhook registered at {Url}", _options.WebhookUrl);
}
```

with:

```csharp
if (isWebhook)
{
    if (string.IsNullOrEmpty(_options.BaseUrl))
        throw new InvalidOperationException(
            "Telegram BaseUrl is required when Mode is 'Webhook'.");

    // Generate webhookId if not yet persisted — first start for this connection
    if (string.IsNullOrEmpty(_options.WebhookId))
    {
        _options.WebhookId = Guid.NewGuid().ToString("N");

        // Persist webhookId back to connection config so it survives restarts
        var connection = _connectionStore.Load(_connectionId);
        if (connection is not null)
        {
            var configDict = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object>>(connection.Config) ?? [];
            configDict["webhookId"] = _options.WebhookId;
            connection.Config = System.Text.Json.JsonSerializer.SerializeToElement(configDict);
            _connectionStore.Save(connection);
            _logger.LogInformation("Telegram: generated webhookId {WebhookId} for connection {ConnectionId}", _options.WebhookId, _connectionId);
        }
    }

    // Compute full webhook URL
    var baseUrl = _options.BaseUrl!.TrimEnd('/');
    var webhookUrl = $"{baseUrl}/api/webhook/telegram/{_options.WebhookId}";

    // Generate webhook secret if not configured
    _webhookSecret = _options.WebhookSecret ?? Guid.NewGuid().ToString("N");

    // Register webhook with Telegram
    await _botClient.SetWebhook(
        webhookUrl,
        secretToken: _webhookSecret,
        cancellationToken: ct);

    _logger.LogInformation("Telegram: webhook registered at {Url}", webhookUrl);
}
```

- [ ] **Step 2: Add public WebhookId property**

Add alongside the existing public properties (around line 39):

```csharp
/// <summary>The webhook ID for this connection, used in the webhook URL path.</summary>
public string? WebhookId => _options.WebhookId;
```

- [ ] **Step 3: Build**

Run: `cd src/agent && dotnet build OpenAgent.Channel.Telegram`
Expected: Build succeeds for the Telegram project. Endpoint still references old route — fixed next.

---

### Task 4: Add GetProviders to IConnectionManager

The webhook endpoint needs to find a provider by `webhookId`, not by `connectionId`. The simplest approach: expose all running providers so the endpoint can scan them.

**Files:**
- Modify: `src/agent/OpenAgent.Contracts/IConnectionManager.cs`
- Modify: `src/agent/OpenAgent/ConnectionManager.cs`

- [ ] **Step 1: Add GetProviders() to interface**

Add to `IConnectionManager`:

```csharp
/// <summary>Returns all running providers.</summary>
IEnumerable<(string ConnectionId, IChannelProvider Provider)> GetProviders();
```

- [ ] **Step 2: Implement in ConnectionManager**

Add to `ConnectionManager`:

```csharp
/// <summary>Returns all running providers.</summary>
public IEnumerable<(string ConnectionId, IChannelProvider Provider)> GetProviders() =>
    _running.Select(kv => (kv.Key, kv.Value));
```

- [ ] **Step 3: Build**

Run: `cd src/agent && dotnet build`
Expected: Build succeeds.

---

### Task 5: Update Webhook Endpoint

**Files:**
- Modify: `src/agent/OpenAgent.Channel.Telegram/TelegramWebhookEndpoints.cs`

- [ ] **Step 1: Rewrite the endpoint**

Replace the full `MapTelegramWebhookEndpoints` method with:

```csharp
/// <summary>
/// Maps POST /api/webhook/telegram/{webhookId} — receives updates from Telegram,
/// validates the secret token, and processes the update asynchronously.
/// Routes by auto-generated webhookId so the user never needs to know connection IDs.
/// </summary>
public static void MapTelegramWebhookEndpoints(this WebApplication app)
{
    app.MapPost("/api/webhook/telegram/{webhookId}", async (
        string webhookId,
        HttpRequest request,
        IConnectionManager connectionManager,
        ILogger<TelegramChannelProvider> logger) =>
    {
        logger.LogInformation("Webhook received for webhookId {WebhookId}", webhookId);

        // Find the running Telegram provider matching this webhookId
        var match = connectionManager.GetProviders()
            .Select(p => p.Provider as TelegramChannelProvider)
            .FirstOrDefault(p => p?.WebhookId is not null &&
                string.Equals(p.WebhookId, webhookId, StringComparison.OrdinalIgnoreCase));

        if (match?.BotClient is null || match.Handler is null)
        {
            logger.LogWarning("Webhook: no running provider for webhookId {WebhookId}", webhookId);
            return Results.NotFound();
        }

        // Validate secret token header (constant-time comparison)
        var secretHeader = request.Headers["X-Telegram-Bot-Api-Secret-Token"].ToString();
        var expectedSecret = match.WebhookSecret ?? string.Empty;
        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(secretHeader),
                Encoding.UTF8.GetBytes(expectedSecret)))
        {
            logger.LogWarning("Webhook: secret token mismatch for webhookId {WebhookId}", webhookId);
            return Results.Unauthorized();
        }

        // Deserialize the Telegram update
        var update = await System.Text.Json.JsonSerializer.DeserializeAsync<Update>(
            request.Body, JsonBotAPI.Options, cancellationToken: request.HttpContext.RequestAborted);

        if (update is null)
        {
            logger.LogWarning("Webhook: failed to deserialize update for webhookId {WebhookId}", webhookId);
            return Results.BadRequest();
        }

        logger.LogInformation("Webhook: processing update {UpdateId} for webhookId {WebhookId}", update.Id, webhookId);

        // Process asynchronously — don't block Telegram
        var sender = match.CreateSender();
        var handler = match.Handler;
        _ = Task.Run(async () =>
        {
            try
            {
                await handler.HandleUpdateAsync(sender, update, CancellationToken.None);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Telegram webhook handler failed for update {UpdateId} on webhookId {WebhookId}",
                    update.Id, webhookId);
            }
        });

        return Results.Ok();
    }).AllowAnonymous();
}
```

- [ ] **Step 2: Build and test**

Run: `cd src/agent && dotnet build && dotnet test`
Expected: Build succeeds. The existing `TelegramWebhookEndpointTests` will fail because they use the old route — fixed in next task.

---

### Task 6: Update Tests

**Files:**
- Modify: `src/agent/OpenAgent.Tests/TelegramWebhookEndpointTests.cs`

- [ ] **Step 1: Update test config and routes**

Update `SetupConnectionAsync` to use `baseUrl` and `webhookId` instead of `webhookUrl`:

```csharp
var telegramConfig = JsonSerializer.SerializeToElement(new
{
    botToken = "123456:ABC-DEF1234ghIkl-zyx57W2v1u123ew11",
    mode = "Webhook",
    baseUrl = "https://example.com",
    webhookId = "test-webhook-id",
    webhookSecret = "test-secret"
});
```

Update all URL references from `/api/connections/{TestConnectionId}/webhook/telegram` to `/api/webhook/telegram/test-webhook-id`.

Update `Webhook_NoRunningConnection_Returns404` to use the new route:

```csharp
var request = new HttpRequestMessage(HttpMethod.Post, "/api/webhook/telegram/nonexistent-id")
```

Update `Webhook_IsAnonymous_NoApiKeyNeeded` to use the new route:

```csharp
var request = new HttpRequestMessage(HttpMethod.Post, "/api/webhook/telegram/nonexistent-id")
```

- [ ] **Step 2: Run all tests**

Run: `cd src/agent && dotnet test`
Expected: All tests pass.

- [ ] **Step 3: Commit**

```bash
git add src/agent/OpenAgent.Channel.Telegram/ src/agent/OpenAgent.Contracts/IConnectionManager.cs src/agent/OpenAgent/ConnectionManager.cs src/agent/OpenAgent.Tests/TelegramWebhookEndpointTests.cs
git commit -m "feat(telegram): auto-generate webhook URL from baseUrl + webhookId

Replace user-provided webhookUrl with auto-generated webhook URLs.
Provider generates a webhookId GUID on first start, persists it to
the connection config, and computes the full URL from baseUrl.
Webhook endpoint route changes to /api/webhook/telegram/{webhookId}.

Co-Authored-By: Claude Opus 4.6 (1M context) <noreply@anthropic.com>"
```

---

### Task 7: Update Settings UI (if needed)

The frontend settings app renders connection forms dynamically from `ConfigFields`. Since we changed `webhookUrl` to `baseUrl` in the factory's `ConfigFields`, the UI should automatically show "Base URL" instead of "Webhook URL". No frontend code changes needed — the dynamic form handles it.

However, after a connection is created and started, the computed webhook URL should be visible somewhere. This could be:
- A read-only field in the connection detail
- Part of the connection API response

This is a nice-to-have for a follow-up — the webhook URL can be derived from `baseUrl` + `webhookId` which are both in the config blob.
