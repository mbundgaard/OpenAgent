# Telnyx Channel Scaffolding — Plan 1

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Create the `OpenAgent.Channel.Telnyx` project skeleton so Telnyx appears as a selectable channel type in the Settings UI with a working (but no-op) lifecycle. No Telnyx API calls yet — that arrives in plan 2.

**Architecture:** Follow the Telegram channel pattern exactly — a new class library implementing `IChannelProvider` (no-op start/stop, logs only) and `IChannelProviderFactory` (declares config fields). DI wiring in `Program.cs` registers the factory. Connection rows persist through the existing `FileConnectionStore` and `IConnectionStore` pipeline, so the Settings UI picks up the new channel type with zero frontend changes (it reads `/api/connections/types`).

**Tech Stack:** .NET 10, xUnit, `Microsoft.AspNetCore.App` framework reference, existing `IChannelProvider` / `IChannelProviderFactory` / `AgentConfig` / `IConversationStore` contracts.

---

## File Structure

New project at `src/agent/OpenAgent.Channel.Telnyx/` with four files:

| File | Responsibility |
|---|---|
| `OpenAgent.Channel.Telnyx.csproj` | Project references to `OpenAgent.Contracts` and `OpenAgent.Models`; framework ref to AspNetCore |
| `TelnyxOptions.cs` | Strongly-typed config: ApiKey, PhoneNumber, WebhookSecret, AllowedNumbers |
| `TelnyxChannelProvider.cs` | `IChannelProvider` — empty `StartAsync`/`StopAsync` that only log. `IOutboundSender` comes in plan 4 |
| `TelnyxChannelProviderFactory.cs` | `IChannelProviderFactory` — declares `Type="telnyx"`, `DisplayName="Telnyx"`, config field schema, parses `JsonElement` into `TelnyxOptions` |

Modifications:

| File | Change |
|---|---|
| `src/agent/OpenAgent.sln` | Add the new project to the solution |
| `src/agent/OpenAgent/OpenAgent.csproj` | `ProjectReference` to the new project |
| `src/agent/OpenAgent/Program.cs` | Register `TelnyxChannelProviderFactory` as `IChannelProviderFactory` (sits alongside Telegram and WhatsApp factories around line 132–138) |
| `src/agent/OpenAgent.Tests/OpenAgent.Tests.csproj` | `ProjectReference` to the new project |

New test file:

| File | Responsibility |
|---|---|
| `src/agent/OpenAgent.Tests/TelnyxChannelProviderFactoryTests.cs` | Verifies `Type`, `DisplayName`, `ConfigFields` shape, and that `Create()` parses a `Connection.Config` `JsonElement` into a working provider |

---

## Task 1: Create the project skeleton

**Files:**
- Create: `src/agent/OpenAgent.Channel.Telnyx/OpenAgent.Channel.Telnyx.csproj`

- [ ] **Step 1: Create the csproj file**

Write `src/agent/OpenAgent.Channel.Telnyx/OpenAgent.Channel.Telnyx.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>
  <ItemGroup>
    <FrameworkReference Include="Microsoft.AspNetCore.App" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\OpenAgent.Contracts\OpenAgent.Contracts.csproj" />
    <ProjectReference Include="..\OpenAgent.Models\OpenAgent.Models.csproj" />
  </ItemGroup>
</Project>
```

Note: no `PackageReference` — this plan is scaffolding only. The Telnyx SDK (or raw `HttpClient`) is added in plan 2.

- [ ] **Step 2: Add the project to the solution**

Run from `src/agent/`:

```bash
dotnet sln OpenAgent.sln add OpenAgent.Channel.Telnyx/OpenAgent.Channel.Telnyx.csproj
```

Expected: `Project ... added to the solution.`

- [ ] **Step 3: Verify it builds**

Run from `src/agent/`:

```bash
dotnet build OpenAgent.Channel.Telnyx/OpenAgent.Channel.Telnyx.csproj
```

Expected: `Build succeeded` with 0 warnings, 0 errors. The project currently has no `.cs` files — that's fine, an empty class library builds.

- [ ] **Step 4: Commit**

```bash
git add src/agent/OpenAgent.Channel.Telnyx/ src/agent/OpenAgent.sln
git commit -m "feat(telnyx): add empty channel project skeleton"
```

---

## Task 2: TelnyxOptions config class

**Files:**
- Create: `src/agent/OpenAgent.Channel.Telnyx/TelnyxOptions.cs`

- [ ] **Step 1: Write the options class**

Write `src/agent/OpenAgent.Channel.Telnyx/TelnyxOptions.cs`:

```csharp
namespace OpenAgent.Channel.Telnyx;

/// <summary>
/// Strongly-typed configuration for a Telnyx channel connection.
/// Populated by <see cref="TelnyxChannelProviderFactory.Create"/> from the
/// connection's JsonElement config blob.
/// </summary>
public sealed class TelnyxOptions
{
    /// <summary>Telnyx API key (v2 key from the portal).</summary>
    public string? ApiKey { get; set; }

    /// <summary>The E.164 phone number this connection owns (e.g. "+4512345678").</summary>
    public string? PhoneNumber { get; set; }

    /// <summary>HMAC secret used to verify inbound webhook signatures.</summary>
    public string? WebhookSecret { get; set; }

    /// <summary>
    /// E.164 numbers allowed to call the agent. Empty list means allow all — caller
    /// restriction is enforced in plan 2 when inbound webhooks land.
    /// </summary>
    // TODO(plan-2): validate E.164 format for PhoneNumber and AllowedNumbers in the factory.
    public List<string> AllowedNumbers { get; set; } = [];
}
```

- [ ] **Step 2: Verify the project still builds**

Run from `src/agent/`:

```bash
dotnet build OpenAgent.Channel.Telnyx/OpenAgent.Channel.Telnyx.csproj
```

Expected: `Build succeeded`.

- [ ] **Step 3: Commit**

```bash
git add src/agent/OpenAgent.Channel.Telnyx/TelnyxOptions.cs
git commit -m "feat(telnyx): add TelnyxOptions config class"
```

---

## Task 3: TelnyxChannelProvider (no-op)

**Files:**
- Create: `src/agent/OpenAgent.Channel.Telnyx/TelnyxChannelProvider.cs`

- [ ] **Step 1: Write the provider**

Write `src/agent/OpenAgent.Channel.Telnyx/TelnyxChannelProvider.cs`:

```csharp
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
```

- [ ] **Step 2: Verify build**

Run from `src/agent/`:

```bash
dotnet build OpenAgent.Channel.Telnyx/OpenAgent.Channel.Telnyx.csproj
```

Expected: `Build succeeded`.

- [ ] **Step 3: Commit**

```bash
git add src/agent/OpenAgent.Channel.Telnyx/TelnyxChannelProvider.cs
git commit -m "feat(telnyx): add no-op channel provider skeleton"
```

---

## Task 4: Factory test (TDD — write the test first)

**Files:**
- Modify: `src/agent/OpenAgent.Tests/OpenAgent.Tests.csproj` — add project reference
- Create: `src/agent/OpenAgent.Tests/TelnyxChannelProviderFactoryTests.cs`

**Note:** Task 4 intentionally ends with a compile-failing test (the factory type doesn't exist yet). Task 5 introduces the factory and commits the test + implementation together — that is the red/green TDD pattern for this plan.

- [ ] **Step 1: Add project reference to the test project**

Open `src/agent/OpenAgent.Tests/OpenAgent.Tests.csproj` and add under the existing `<ItemGroup>` that contains the other channel ProjectReferences:

```xml
<ProjectReference Include="..\OpenAgent.Channel.Telnyx\OpenAgent.Channel.Telnyx.csproj" />
```

If uncertain where the group is, run:

```bash
grep -n "OpenAgent.Channel.Telegram" src/agent/OpenAgent.Tests/OpenAgent.Tests.csproj
```

Add the new line immediately after the matching Telegram reference so channel refs stay grouped.

- [ ] **Step 2: Write the failing factory test**

Write `src/agent/OpenAgent.Tests/TelnyxChannelProviderFactoryTests.cs`:

```csharp
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using OpenAgent.Channel.Telnyx;
using OpenAgent.Models.Connections;
using Xunit;

namespace OpenAgent.Tests;

public class TelnyxChannelProviderFactoryTests
{
    [Fact]
    public void Factory_exposes_expected_metadata()
    {
        var factory = new TelnyxChannelProviderFactory(NullLoggerFactory.Instance);

        Assert.Equal("telnyx", factory.Type);
        Assert.Equal("Telnyx", factory.DisplayName);
        Assert.Null(factory.SetupStep);
    }

    [Fact]
    public void ConfigFields_declares_expected_keys()
    {
        var factory = new TelnyxChannelProviderFactory(NullLoggerFactory.Instance);

        var keys = factory.ConfigFields.Select(f => f.Key).ToArray();

        Assert.Contains("apiKey", keys);
        Assert.Contains("phoneNumber", keys);
        Assert.Contains("webhookSecret", keys);
        Assert.Contains("allowedNumbers", keys);
    }

    [Fact]
    public void ApiKey_and_webhookSecret_are_secret_fields()
    {
        var factory = new TelnyxChannelProviderFactory(NullLoggerFactory.Instance);

        var apiKey = factory.ConfigFields.Single(f => f.Key == "apiKey");
        var secret = factory.ConfigFields.Single(f => f.Key == "webhookSecret");

        Assert.Equal("Secret", apiKey.Type);
        Assert.True(apiKey.Required);
        Assert.Equal("Secret", secret.Type);
    }

    [Fact]
    public void Create_parses_string_config_into_options()
    {
        var factory = new TelnyxChannelProviderFactory(NullLoggerFactory.Instance);
        var config = JsonDocument.Parse("""
            {
                "apiKey": "KEY_abc",
                "phoneNumber": "+4512345678",
                "webhookSecret": "shh",
                "allowedNumbers": "+4511111111,+4522222222"
            }
            """).RootElement;
        var connection = new Connection
        {
            Id = "conn-1",
            Name = "Test",
            Type = "telnyx",
            Enabled = true,
            ConversationId = "conv-1",
            Config = config,
        };

        var provider = (TelnyxChannelProvider)factory.Create(connection);

        Assert.Equal("KEY_abc", provider.Options.ApiKey);
        Assert.Equal("+4512345678", provider.Options.PhoneNumber);
        Assert.Equal("shh", provider.Options.WebhookSecret);
        Assert.Equal(new[] { "+4511111111", "+4522222222" }, provider.Options.AllowedNumbers);
        Assert.Equal("conn-1", provider.ConnectionId);
    }

    [Fact]
    public void Create_parses_array_allowedNumbers()
    {
        var factory = new TelnyxChannelProviderFactory(NullLoggerFactory.Instance);
        var config = JsonDocument.Parse("""
            {
                "apiKey": "k",
                "allowedNumbers": ["+4511111111", "+4522222222"]
            }
            """).RootElement;
        var connection = new Connection
        {
            Id = "conn-2",
            Name = "Test",
            Type = "telnyx",
            Enabled = true,
            ConversationId = "conv-2",
            Config = config,
        };

        var provider = (TelnyxChannelProvider)factory.Create(connection);

        Assert.Equal(new[] { "+4511111111", "+4522222222" }, provider.Options.AllowedNumbers);
    }

    [Fact]
    public void Create_handles_missing_optional_fields()
    {
        var factory = new TelnyxChannelProviderFactory(NullLoggerFactory.Instance);
        var config = JsonDocument.Parse("""{ "apiKey": "k" }""").RootElement;
        var connection = new Connection
        {
            Id = "conn-3",
            Name = "Test",
            Type = "telnyx",
            Enabled = true,
            ConversationId = "conv-3",
            Config = config,
        };

        var provider = (TelnyxChannelProvider)factory.Create(connection);

        Assert.Equal("k", provider.Options.ApiKey);
        Assert.Null(provider.Options.PhoneNumber);
        Assert.Null(provider.Options.WebhookSecret);
        Assert.Empty(provider.Options.AllowedNumbers);
    }
}
```

- [ ] **Step 3: Run the tests and confirm they fail**

Run from `src/agent/`:

```bash
dotnet test OpenAgent.Tests/OpenAgent.Tests.csproj --filter "FullyQualifiedName~TelnyxChannelProviderFactoryTests"
```

Expected: compile error — `TelnyxChannelProviderFactory` does not exist yet. That is the failing-test state.

---

## Task 5: Implement TelnyxChannelProviderFactory

**Files:**
- Create: `src/agent/OpenAgent.Channel.Telnyx/TelnyxChannelProviderFactory.cs`

- [ ] **Step 1: Write the factory**

Write `src/agent/OpenAgent.Channel.Telnyx/TelnyxChannelProviderFactory.cs`:

```csharp
using System.Text.Json;
using Microsoft.Extensions.Logging;
using OpenAgent.Contracts;
using OpenAgent.Models.Connections;
using OpenAgent.Models.Providers;

namespace OpenAgent.Channel.Telnyx;

/// <summary>
/// Creates <see cref="TelnyxChannelProvider"/> instances from connection configuration.
/// </summary>
public sealed class TelnyxChannelProviderFactory : IChannelProviderFactory
{
    private readonly ILoggerFactory _loggerFactory;

    public string Type => "telnyx";

    public string DisplayName => "Telnyx";

    public IReadOnlyList<ProviderConfigField> ConfigFields { get; } =
    [
        new() { Key = "apiKey", Label = "API Key", Type = "Secret", Required = true },
        new() { Key = "phoneNumber", Label = "Phone Number (E.164)", Type = "String", Required = true },
        new() { Key = "webhookSecret", Label = "Webhook Signing Secret", Type = "Secret" },
        new() { Key = "allowedNumbers", Label = "Allowed Caller Numbers (comma-separated, empty = allow all)", Type = "String" },
    ];

    public ChannelSetupStep? SetupStep => null;

    public TelnyxChannelProviderFactory(ILoggerFactory loggerFactory)
    {
        _loggerFactory = loggerFactory;
    }

    /// <summary>Deserializes the connection's config into TelnyxOptions and creates the provider.</summary>
    public IChannelProvider Create(Connection connection)
    {
        // Parse config manually — the dynamic form sends comma-separated strings
        // for list fields. Mirrors TelegramChannelProviderFactory.Create.
        var options = new TelnyxOptions();

        if (connection.Config.ValueKind == JsonValueKind.Object)
        {
            if (connection.Config.TryGetProperty("apiKey", out var keyEl) && keyEl.ValueKind == JsonValueKind.String)
                options.ApiKey = keyEl.GetString();

            if (connection.Config.TryGetProperty("phoneNumber", out var phoneEl) && phoneEl.ValueKind == JsonValueKind.String)
                options.PhoneNumber = phoneEl.GetString();

            if (connection.Config.TryGetProperty("webhookSecret", out var secretEl) && secretEl.ValueKind == JsonValueKind.String)
                options.WebhookSecret = secretEl.GetString();

            if (connection.Config.TryGetProperty("allowedNumbers", out var allowedEl))
            {
                if (allowedEl.ValueKind == JsonValueKind.Array)
                    options.AllowedNumbers = allowedEl.EnumerateArray()
                        .Where(e => e.ValueKind == JsonValueKind.String)
                        .Select(e => e.GetString()!)
                        .Where(s => s.Length > 0)
                        .ToList();
                else if (allowedEl.ValueKind == JsonValueKind.String)
                {
                    var raw = allowedEl.GetString() ?? "";
                    options.AllowedNumbers = raw.Length > 0
                        ? raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList()
                        : [];
                }
            }
        }

        return new TelnyxChannelProvider(
            options,
            connection.Id,
            _loggerFactory.CreateLogger<TelnyxChannelProvider>());
    }
}
```

- [ ] **Step 2: Run the tests and confirm they pass**

Run from `src/agent/`:

```bash
dotnet test OpenAgent.Tests/OpenAgent.Tests.csproj --filter "FullyQualifiedName~TelnyxChannelProviderFactoryTests"
```

Expected: 6 tests, all pass.

- [ ] **Step 3: Commit**

```bash
git add src/agent/OpenAgent.Channel.Telnyx/TelnyxChannelProviderFactory.cs \
        src/agent/OpenAgent.Tests/TelnyxChannelProviderFactoryTests.cs \
        src/agent/OpenAgent.Tests/OpenAgent.Tests.csproj
git commit -m "feat(telnyx): factory with config schema and JsonElement parsing"
```

---

## Task 6: Wire the factory into DI

**Files:**
- Modify: `src/agent/OpenAgent/OpenAgent.csproj` — add project reference
- Modify: `src/agent/OpenAgent/Program.cs` — register factory alongside Telegram/WhatsApp

- [ ] **Step 1: Add project reference from the host**

Open `src/agent/OpenAgent/OpenAgent.csproj`. Locate the ItemGroup containing `OpenAgent.Channel.Telegram`:

```bash
grep -n "OpenAgent.Channel" src/agent/OpenAgent/OpenAgent.csproj
```

Add immediately after the Telegram entry:

```xml
<ProjectReference Include="..\OpenAgent.Channel.Telnyx\OpenAgent.Channel.Telnyx.csproj" />
```

- [ ] **Step 2: Add using + factory registration in Program.cs**

Open `src/agent/OpenAgent/Program.cs`.

Add `using OpenAgent.Channel.Telnyx;` alongside the existing `using OpenAgent.Channel.Telegram;` near the top of the file.

Find the insertion point — run:

```bash
grep -n "WhatsAppChannelProviderFactory\|TelegramChannelProviderFactory" src/agent/OpenAgent/Program.cs
```

The Telegram factory registration (around line 132–138) looks like:

```csharp
builder.Services.AddSingleton<IChannelProviderFactory>(sp =>
    new TelegramChannelProviderFactory(
        sp.GetRequiredService<IConversationStore>(),
        sp.GetRequiredService<IConnectionStore>(),
        sp.GetRequiredService<Func<string, ILlmTextProvider>>(),
        sp.GetRequiredService<AgentConfig>(),
        sp.GetRequiredService<ILoggerFactory>()));
```

Immediately after the WhatsApp factory registration (the next `AddSingleton<IChannelProviderFactory>` block), add:

```csharp
builder.Services.AddSingleton<IChannelProviderFactory>(sp =>
    new TelnyxChannelProviderFactory(
        sp.GetRequiredService<ILoggerFactory>()));
```

- [ ] **Step 3: Build the host project**

Run from `src/agent/`:

```bash
dotnet build OpenAgent/OpenAgent.csproj
```

Expected: `Build succeeded`.

- [ ] **Step 4: Run the full test suite**

Run from `src/agent/`:

```bash
dotnet test
```

Expected: all tests pass (existing suite + 6 new Telnyx factory tests).

- [ ] **Step 5: Commit**

```bash
git add src/agent/OpenAgent/OpenAgent.csproj src/agent/OpenAgent/Program.cs
git commit -m "feat(telnyx): register channel factory in DI"
```

---

## Task 7: End-to-end verification against the running app

**Files:** none modified — verification only.

- [ ] **Step 1: Start the agent locally**

Run from `src/agent/OpenAgent/`:

```bash
dotnet run
```

Wait for `Now listening on: http://localhost:<port>`.

- [ ] **Step 2: Verify Telnyx appears in connection types**

In a second shell, read the dev API key from `src/agent/OpenAgent/appsettings.Development.json` (field `Authentication:ApiKey`), then:

```bash
curl -s -H "X-Api-Key: <devkey>" http://localhost:<port>/api/connections/types | jq '.[] | select(.type=="telnyx")'
```

Expected: a JSON object with `type: "telnyx"`, `displayName: "Telnyx"`, and a `configFields` array containing `apiKey`, `phoneNumber`, `webhookSecret`, `allowedNumbers`.

If `jq` is unavailable, pipe through `python -m json.tool` instead (note: Windows uses `python`, not `python3`).

- [ ] **Step 3: Create a Telnyx connection and start it**

```bash
curl -s -X POST -H "X-Api-Key: <devkey>" -H "Content-Type: application/json" \
  -d '{"name":"Test Telnyx","type":"telnyx","enabled":false,"config":{"apiKey":"TEST","phoneNumber":"+4512345678","webhookSecret":"sss","allowedNumbers":""}}' \
  http://localhost:<port>/api/connections
```

Note: `conversationId` is intentionally omitted from this body — `POST /api/connections` auto-generates a GUID when missing (`ConnectionEndpoints.cs` does `request.ConversationId ?? Guid.NewGuid().ToString()`).

Note the returned `id`. Then start it:

```bash
curl -s -X POST -H "X-Api-Key: <devkey>" http://localhost:<port>/api/connections/<id>/start
```

Expected: HTTP 200. In the agent log output you should see a line similar to:

```
Telnyx channel <id> started (phoneNumber=+4512345678, allowedCount=0)
```

Stop it:

```bash
curl -s -X POST -H "X-Api-Key: <devkey>" http://localhost:<port>/api/connections/<id>/stop
```

Expected log line:

```
Telnyx channel <id> stopped
```

- [ ] **Step 4: Clean up the test connection**

```bash
curl -s -X DELETE -H "X-Api-Key: <devkey>" http://localhost:<port>/api/connections/<id>
```

Expected: HTTP 200 or 204.

- [ ] **Step 5: Stop the dev server**

Return to the dotnet run shell and press Ctrl+C.

No commit for this task — verification only.

---

## Done criteria

- [ ] `OpenAgent.Channel.Telnyx` builds as part of the solution
- [ ] `TelnyxChannelProviderFactory` exposes `type="telnyx"`, `displayName="Telnyx"`, and the four config fields
- [ ] Factory parses both string and array forms of `allowedNumbers`
- [ ] `TelnyxChannelProvider` logs on start/stop (no Telnyx traffic)
- [ ] Factory registered in `Program.cs` alongside Telegram/WhatsApp
- [ ] `/api/connections/types` returns Telnyx metadata
- [ ] Settings UI renders the new channel form (no frontend changes needed)
- [ ] Full test suite passes

Plan 2 (inbound calls via TeXML) will build on this scaffolding to add webhook endpoints, call answering, and text-mode voice via Telnyx's STT/TTS.
