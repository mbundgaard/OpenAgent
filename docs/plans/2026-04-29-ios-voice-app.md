# iOS Voice App Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Ship a phone-app-style iOS-only .NET MAUI client that lets the user pick or create a conversation and have a voice call with an OpenAgent instance over its existing voice WebSocket.

**Architecture:** Two-project split — `OpenAgent.App.Core` (`net10.0`, no MAUI deps, fully testable on Windows) holds DTOs, API/WebSocket clients, state machines, parsers, cache. `OpenAgent.App` (`net10.0-ios` MAUI head) holds Pages, ViewModels, iOS Keychain, and AVAudioEngine glue. Build runs on a hosted GitHub Actions macOS runner; tagged builds upload to TestFlight.

**Tech Stack:** .NET 10, MAUI iOS, CommunityToolkit.Mvvm, ZXing.Net.Maui (QR), System.Net.WebSockets, xUnit. iOS frameworks: AVFoundation, Security (Keychain).

**Design doc:** `docs/plans/2026-04-29-ios-voice-app-design.md`

---

## Conventions for this plan

- All paths are relative to repo root unless stated.
- Each task is TDD where the code is testable from Windows. iOS-specific tasks (audio, Keychain, camera) are scaffolded but not unit-tested — they're verified manually on a TestFlight build at the end.
- Commit after every task. One-line commit message in conventional style (`feat:`, `test:`, `chore:`, `docs:`).
- Use `dotnet test` from `src/app/` to run all Core tests. The MAUI head project is excluded from `dotnet test` (it can't run on Windows).
- When a task says "run the test", use `dotnet test --filter FullyQualifiedName~<TestName>` to scope.
- No emojis in code or commits. XML doc comments on public types. `[JsonPropertyName]` on serialized models.

---

## Phase 0 — Scaffolding

### Task 1: Create solution and Core/Tests projects

**Files:**
- Create: `src/app/OpenAgent.App.sln`
- Create: `src/app/Directory.Packages.props`
- Create: `src/app/Directory.Build.props`
- Create: `src/app/OpenAgent.App.Core/OpenAgent.App.Core.csproj`
- Create: `src/app/OpenAgent.App.Tests/OpenAgent.App.Tests.csproj`
- Create: `src/app/OpenAgent.App.Core/AssemblyInfo.cs`
- Create: `src/app/OpenAgent.App.Tests/UsingsTests.cs` (xunit usings)
- Create: `.gitignore` additions for MAUI build outputs (`src/app/**/bin/`, `src/app/**/obj/`)

**Step 1: Create the directory and stub solution.**

```bash
mkdir -p src/app/OpenAgent.App.Core src/app/OpenAgent.App.Tests
cd src/app && dotnet new sln -n OpenAgent.App
```

**Step 2: Write `Directory.Packages.props`** at `src/app/Directory.Packages.props`:

```xml
<Project>
  <PropertyGroup>
    <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
  </PropertyGroup>
  <ItemGroup>
    <PackageVersion Include="CommunityToolkit.Mvvm" Version="8.4.0" />
    <PackageVersion Include="Microsoft.Extensions.DependencyInjection" Version="10.0.3" />
    <PackageVersion Include="Microsoft.Extensions.Logging.Abstractions" Version="10.0.3" />
    <PackageVersion Include="Microsoft.Maui.Controls" Version="10.0.0" />
    <PackageVersion Include="Microsoft.Maui.Controls.Compatibility" Version="10.0.0" />
    <PackageVersion Include="Microsoft.Extensions.Logging.Debug" Version="10.0.3" />
    <PackageVersion Include="ZXing.Net.Maui" Version="0.4.0" />
    <PackageVersion Include="ZXing.Net.Maui.Controls" Version="0.4.0" />
    <PackageVersion Include="Microsoft.NET.Test.Sdk" Version="17.14.1" />
    <PackageVersion Include="xunit" Version="2.9.3" />
    <PackageVersion Include="xunit.runner.visualstudio" Version="3.1.4" />
    <PackageVersion Include="coverlet.collector" Version="6.0.4" />
  </ItemGroup>
</Project>
```

**Step 3: Write `Directory.Build.props`** at `src/app/Directory.Build.props`:

```xml
<Project>
  <PropertyGroup>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <LangVersion>latest</LangVersion>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
  </PropertyGroup>
</Project>
```

**Step 4: Write `OpenAgent.App.Core/OpenAgent.App.Core.csproj`:**

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <RootNamespace>OpenAgent.App.Core</RootNamespace>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.Extensions.Logging.Abstractions" />
  </ItemGroup>
</Project>
```

**Step 5: Write `OpenAgent.App.Tests/OpenAgent.App.Tests.csproj`:**

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <IsPackable>false</IsPackable>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" />
    <PackageReference Include="xunit" />
    <PackageReference Include="xunit.runner.visualstudio" />
    <PackageReference Include="coverlet.collector" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\OpenAgent.App.Core\OpenAgent.App.Core.csproj" />
  </ItemGroup>
</Project>
```

**Step 6: Add to solution + add gitignore lines.**

```bash
cd src/app
dotnet sln add OpenAgent.App.Core/OpenAgent.App.Core.csproj
dotnet sln add OpenAgent.App.Tests/OpenAgent.App.Tests.csproj
```

Append to repo-root `.gitignore`:

```
# MAUI app
src/app/**/bin/
src/app/**/obj/
src/app/**/*.user
```

**Step 7: Verify build + test.**

Run: `cd src/app && dotnet build`
Expected: build succeeds, no projects to test yet.

Run: `cd src/app && dotnet test`
Expected: "No test is available" or 0 tests.

**Step 8: Commit.**

```bash
git add src/app .gitignore
git commit -m "chore(app): scaffold OpenAgent.App.Core and OpenAgent.App.Tests"
```

---

## Phase 1 — Core models, parsers, state (Windows-testable, TDD)

> **Contract lock-in.** Before writing any DTO or parser in this phase, open `src/agent/OpenAgent.Models/Conversations/ConversationResponses.cs` and `src/agent/OpenAgent.Models/Voice/VoiceWebSocketContracts.cs` once and copy field names verbatim. The fixtures and DTOs below are already aligned to those files as of 2026-04-29; if they have drifted, fix the fixture and DTO together so the round-trip test asserts a real contract.

### Task 2: QR payload parser

**Files:**
- Create: `src/app/OpenAgent.App.Core/Onboarding/QrPayload.cs`
- Create: `src/app/OpenAgent.App.Core/Onboarding/QrPayloadParser.cs`
- Test: `src/app/OpenAgent.App.Tests/Onboarding/QrPayloadParserTests.cs`

**Step 1: Write the failing tests** at `OpenAgent.App.Tests/Onboarding/QrPayloadParserTests.cs`:

```csharp
using OpenAgent.App.Core.Onboarding;

namespace OpenAgent.App.Tests.Onboarding;

public class QrPayloadParserTests
{
    [Theory]
    [InlineData("https://host.example/?token=abc", "https://host.example/", "abc")]
    [InlineData("http://localhost:8080/?token=xyz", "http://localhost:8080/", "xyz")]
    [InlineData("https://host.example/#token=hashed", "https://host.example/", "hashed")]
    [InlineData("https://host.example:443/sub/?token=t", "https://host.example:443/sub/", "t")]
    public void Parses_url_and_token(string input, string expectedBase, string expectedToken)
    {
        var ok = QrPayloadParser.TryParse(input, out var payload, out _);
        Assert.True(ok);
        Assert.Equal(expectedBase, payload!.BaseUrl);
        Assert.Equal(expectedToken, payload.Token);
    }

    [Theory]
    [InlineData("not a url")]
    [InlineData("https://host.example/")]            // no token
    [InlineData("ftp://host.example/?token=x")]      // wrong scheme
    [InlineData("")]
    [InlineData("https://host.example/?token=")]     // empty token
    public void Rejects_malformed(string input)
    {
        var ok = QrPayloadParser.TryParse(input, out _, out var error);
        Assert.False(ok);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }
}
```

**Step 2: Run test, expect failure.**

Run: `cd src/app && dotnet test --filter FullyQualifiedName~QrPayloadParserTests`
Expected: FAIL — `QrPayloadParser` does not exist.

**Step 3: Implement `QrPayload`** at `OpenAgent.App.Core/Onboarding/QrPayload.cs`:

```csharp
namespace OpenAgent.App.Core.Onboarding;

/// <summary>Parsed credentials from a QR code or manual entry.</summary>
public sealed record QrPayload(string BaseUrl, string Token);
```

**Step 4: Implement `QrPayloadParser`** at `OpenAgent.App.Core/Onboarding/QrPayloadParser.cs`:

```csharp
namespace OpenAgent.App.Core.Onboarding;

/// <summary>Parses agent connection URLs of the form https://host[:port]/[path]?token=... (or with #token=).</summary>
public static class QrPayloadParser
{
    public static bool TryParse(string input, out QrPayload? payload, out string error)
    {
        payload = null;
        error = "";

        if (string.IsNullOrWhiteSpace(input))
        {
            error = "Empty input";
            return false;
        }

        if (!Uri.TryCreate(input, UriKind.Absolute, out var uri))
        {
            error = "Not a valid URL";
            return false;
        }

        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
        {
            error = $"Unsupported scheme: {uri.Scheme}";
            return false;
        }

        var token = ExtractToken(uri);
        if (string.IsNullOrWhiteSpace(token))
        {
            error = "Missing token";
            return false;
        }

        var basePart = $"{uri.Scheme}://{uri.Authority}{uri.AbsolutePath}";
        if (!basePart.EndsWith('/')) basePart += "/";

        payload = new QrPayload(basePart, token);
        return true;
    }

    private static string? ExtractToken(Uri uri)
    {
        // Accept ?token=...
        if (!string.IsNullOrEmpty(uri.Query))
        {
            foreach (var pair in uri.Query.TrimStart('?').Split('&'))
            {
                var eq = pair.IndexOf('=');
                if (eq <= 0) continue;
                if (pair[..eq] != "token") continue;
                return Uri.UnescapeDataString(pair[(eq + 1)..]);
            }
        }

        // Accept #token=... (matches the agent's startup print format)
        if (!string.IsNullOrEmpty(uri.Fragment))
        {
            var frag = uri.Fragment.TrimStart('#');
            foreach (var pair in frag.Split('&'))
            {
                var eq = pair.IndexOf('=');
                if (eq <= 0) continue;
                if (pair[..eq] != "token") continue;
                return Uri.UnescapeDataString(pair[(eq + 1)..]);
            }
        }

        return null;
    }
}
```

**Step 5: Run test, expect pass.**

Run: `cd src/app && dotnet test --filter FullyQualifiedName~QrPayloadParserTests`
Expected: PASS, 9 tests.

**Step 6: Commit.**

```bash
git add src/app/OpenAgent.App.Core/Onboarding src/app/OpenAgent.App.Tests/Onboarding
git commit -m "feat(app-core): QR payload parser"
```

---

### Task 3: Credential store interface + in-memory implementation

**Files:**
- Create: `src/app/OpenAgent.App.Core/Services/ICredentialStore.cs`
- Create: `src/app/OpenAgent.App.Core/Services/InMemoryCredentialStore.cs`
- Test: `src/app/OpenAgent.App.Tests/Services/InMemoryCredentialStoreTests.cs`

**Step 1: Write tests.**

```csharp
using OpenAgent.App.Core.Onboarding;
using OpenAgent.App.Core.Services;

namespace OpenAgent.App.Tests.Services;

public class InMemoryCredentialStoreTests
{
    [Fact]
    public async Task Round_trip_returns_what_was_saved()
    {
        var store = new InMemoryCredentialStore();
        await store.SaveAsync(new QrPayload("https://h/", "tok"));
        var got = await store.LoadAsync();
        Assert.Equal("https://h/", got!.BaseUrl);
        Assert.Equal("tok", got.Token);
    }

    [Fact]
    public async Task Load_on_empty_returns_null()
    {
        var store = new InMemoryCredentialStore();
        Assert.Null(await store.LoadAsync());
    }

    [Fact]
    public async Task Clear_removes_credentials()
    {
        var store = new InMemoryCredentialStore();
        await store.SaveAsync(new QrPayload("https://h/", "tok"));
        await store.ClearAsync();
        Assert.Null(await store.LoadAsync());
    }
}
```

**Step 2: Run, expect FAIL.**

**Step 3: Implement interface** at `OpenAgent.App.Core/Services/ICredentialStore.cs`:

```csharp
using OpenAgent.App.Core.Onboarding;

namespace OpenAgent.App.Core.Services;

/// <summary>Stores the agent base URL + API token. iOS impl uses Keychain; tests use in-memory.</summary>
public interface ICredentialStore
{
    Task<QrPayload?> LoadAsync(CancellationToken ct = default);
    Task SaveAsync(QrPayload payload, CancellationToken ct = default);
    Task ClearAsync(CancellationToken ct = default);
}
```

**Step 4: Implement `InMemoryCredentialStore`:**

```csharp
using OpenAgent.App.Core.Onboarding;

namespace OpenAgent.App.Core.Services;

public sealed class InMemoryCredentialStore : ICredentialStore
{
    private QrPayload? _current;
    public Task<QrPayload?> LoadAsync(CancellationToken ct = default) => Task.FromResult(_current);
    public Task SaveAsync(QrPayload payload, CancellationToken ct = default) { _current = payload; return Task.CompletedTask; }
    public Task ClearAsync(CancellationToken ct = default) { _current = null; return Task.CompletedTask; }
}
```

**Step 5: Run, expect PASS.**

**Step 6: Commit.**

```bash
git commit -am "feat(app-core): ICredentialStore + InMemory impl"
```

---

### Task 4: Conversation list DTOs + JSON round-trip

**Files:**
- Create: `src/app/OpenAgent.App.Core/Models/ConversationListItem.cs`
- Create: `src/app/OpenAgent.App.Core/Models/JsonOptions.cs`
- Test: `src/app/OpenAgent.App.Tests/Models/ConversationListItemTests.cs`
- Test fixture: `src/app/OpenAgent.App.Tests/Fixtures/conversation-list.json`

> **Field names verified against** `src/agent/OpenAgent.Models/Conversations/ConversationResponses.cs:8-58` on 2026-04-29: `id`, `source`, `created_at`, `turn_count`, `last_activity` (nullable), `display_name` (optional), `intention` (optional). No `last_message_at` or `message_count` on the wire.

**Step 1: Fixture** at `src/app/OpenAgent.App.Tests/Fixtures/conversation-list.json`:

```json
[
  {
    "id": "abc-123",
    "source": "telegram",
    "text_provider": "openai",
    "text_model": "gpt-4o",
    "voice_provider": "openai",
    "voice_model": "gpt-4o-realtime",
    "created_at": "2026-04-28T10:00:00Z",
    "total_prompt_tokens": 0,
    "total_completion_tokens": 0,
    "turn_count": 14,
    "last_activity": "2026-04-29T08:30:00Z",
    "display_name": "Pricing chat",
    "intention": "Discuss pricing tiers"
  },
  {
    "id": "def-456",
    "source": "app",
    "text_provider": "openai",
    "text_model": "gpt-4o",
    "voice_provider": "openai",
    "voice_model": "gpt-4o-realtime",
    "created_at": "2026-04-29T09:00:00Z",
    "total_prompt_tokens": 0,
    "total_completion_tokens": 0,
    "turn_count": 0
  }
]
```

**Step 2: Mark fixtures as content** — add to `OpenAgent.App.Tests.csproj` `<ItemGroup>`:

```xml
<None Update="Fixtures\**\*.json"><CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory></None>
```

**Step 3: Write test.**

```csharp
using System.Text.Json;
using OpenAgent.App.Core.Models;

namespace OpenAgent.App.Tests.Models;

public class ConversationListItemTests
{
    [Fact]
    public void Round_trip_against_canned_fixture()
    {
        var json = File.ReadAllText("Fixtures/conversation-list.json");
        var items = JsonSerializer.Deserialize<List<ConversationListItem>>(json, JsonOptions.Default);
        Assert.NotNull(items);
        Assert.Equal(2, items!.Count);
        Assert.Equal("abc-123", items[0].Id);
        Assert.Equal("telegram", items[0].Source);
        Assert.Equal("Pricing chat", items[0].DisplayName);
        Assert.Equal("Discuss pricing tiers", items[0].Intention);
        Assert.Equal(14, items[0].TurnCount);
        Assert.Equal(new DateTimeOffset(2026, 4, 29, 8, 30, 0, TimeSpan.Zero), items[0].LastActivity);
        Assert.Null(items[1].DisplayName);
        Assert.Null(items[1].Intention);
        Assert.Null(items[1].LastActivity);
    }

    [Fact]
    public void Title_helper_falls_back_through_display_name_intention_id()
    {
        var item = new ConversationListItem { Id = "id-1", Source = "app", CreatedAt = DateTimeOffset.UtcNow };
        Assert.Equal("id-1", item.Title);

        var withIntention = item with { Intention = "I" };
        Assert.Equal("I", withIntention.Title);

        var withDisplay = withIntention with { DisplayName = "D" };
        Assert.Equal("D", withDisplay.Title);
    }
}
```

**Step 4: Run, expect FAIL.**

**Step 5: Implement.** `OpenAgent.App.Core/Models/JsonOptions.cs`:

```csharp
using System.Text.Json;

namespace OpenAgent.App.Core.Models;

/// <summary>Snake-case JSON options matching the agent's wire format.</summary>
public static class JsonOptions
{
    public static readonly JsonSerializerOptions Default = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };
}
```

`OpenAgent.App.Core/Models/ConversationListItem.cs`:

```csharp
using System.Text.Json.Serialization;

namespace OpenAgent.App.Core.Models;

/// <summary>One row as returned by GET /api/conversations. Field names mirror ConversationListItemResponse on the agent.</summary>
public sealed record ConversationListItem
{
    [JsonPropertyName("id")] public required string Id { get; init; }
    [JsonPropertyName("source")] public required string Source { get; init; }
    [JsonPropertyName("created_at")] public required DateTimeOffset CreatedAt { get; init; }
    [JsonPropertyName("last_activity")] public DateTimeOffset? LastActivity { get; init; }
    [JsonPropertyName("turn_count")] public int TurnCount { get; init; }
    [JsonPropertyName("display_name")] public string? DisplayName { get; init; }
    [JsonPropertyName("intention")] public string? Intention { get; init; }

    /// <summary>Effective row title — display_name wins, then intention, then id.</summary>
    [JsonIgnore]
    public string Title => !string.IsNullOrWhiteSpace(DisplayName) ? DisplayName!
                          : !string.IsNullOrWhiteSpace(Intention) ? Intention!
                          : Id;

    /// <summary>Effective sort key for list ordering — last_activity if known, else created_at.</summary>
    [JsonIgnore]
    public DateTimeOffset SortKey => LastActivity ?? CreatedAt;
}
```

The DTO drops several wire-only fields (token counts, providers/models). They round-trip cleanly because `System.Text.Json` ignores unknown JSON properties by default; we only deserialize what we render. Fields can be added later when the UI needs them.

**Step 6: Run, expect PASS.**

**Step 7: Commit.**

```bash
git commit -am "feat(app-core): ConversationListItem DTO matching agent contract"
```

---

### Task 5: Voice event union + JSON parsing

**Files:**
- Create: `src/app/OpenAgent.App.Core/Voice/VoiceEvent.cs`
- Create: `src/app/OpenAgent.App.Core/Voice/VoiceEventParser.cs`
- Test: `src/app/OpenAgent.App.Tests/Voice/VoiceEventParserTests.cs`

**Step 1: Tests** — feed each JSON shape from the agent (see `WebSocketVoiceEndpoints.cs:128-187`) and assert the parsed type:

```csharp
using OpenAgent.App.Core.Voice;

namespace OpenAgent.App.Tests.Voice;

public class VoiceEventParserTests
{
    [Fact]
    public void Parses_session_ready()
    {
        var json = """{"type":"session_ready","input_sample_rate":24000,"output_sample_rate":24000,"input_codec":"pcm16","output_codec":"pcm16"}""";
        var evt = VoiceEventParser.Parse(json);
        var ready = Assert.IsType<VoiceEvent.SessionReady>(evt);
        Assert.Equal(24000, ready.InputSampleRate);
        Assert.Equal("pcm16", ready.InputCodec);
    }

    [Theory]
    [InlineData("speech_started", typeof(VoiceEvent.SpeechStarted))]
    [InlineData("speech_stopped", typeof(VoiceEvent.SpeechStopped))]
    [InlineData("audio_done", typeof(VoiceEvent.AudioDone))]
    [InlineData("thinking_started", typeof(VoiceEvent.ThinkingStarted))]
    [InlineData("thinking_stopped", typeof(VoiceEvent.ThinkingStopped))]
    public void Parses_simple_signals(string type, Type expected)
    {
        var evt = VoiceEventParser.Parse($"{{\"type\":\"{type}\"}}");
        Assert.IsType(expected, evt);
    }

    [Fact]
    public void Parses_transcript_delta()
    {
        var json = """{"type":"transcript_delta","text":"hello","source":"user"}""";
        var evt = VoiceEventParser.Parse(json);
        var t = Assert.IsType<VoiceEvent.TranscriptDelta>(evt);
        Assert.Equal("hello", t.Text);
        Assert.Equal(TranscriptSource.User, t.Source);
    }

    [Fact]
    public void Parses_transcript_done() { /* same shape, type=transcript_done */ }

    [Fact]
    public void Parses_error()
    {
        var evt = VoiceEventParser.Parse("""{"type":"error","message":"boom"}""");
        var e = Assert.IsType<VoiceEvent.Error>(evt);
        Assert.Equal("boom", e.Message);
    }

    [Fact]
    public void Unknown_type_returns_null()
    {
        Assert.Null(VoiceEventParser.Parse("""{"type":"future_thing"}"""));
    }
}
```

**Step 2: Run, expect FAIL.**

**Step 3: Implement** `OpenAgent.App.Core/Voice/VoiceEvent.cs`:

```csharp
namespace OpenAgent.App.Core.Voice;

public enum TranscriptSource { User, Assistant }

/// <summary>Closed union of voice WebSocket text-frame events.</summary>
public abstract record VoiceEvent
{
    public sealed record SessionReady(int InputSampleRate, int OutputSampleRate, string InputCodec, string OutputCodec) : VoiceEvent;
    public sealed record SpeechStarted : VoiceEvent;
    public sealed record SpeechStopped : VoiceEvent;
    public sealed record AudioDone : VoiceEvent;
    public sealed record ThinkingStarted : VoiceEvent;
    public sealed record ThinkingStopped : VoiceEvent;
    public sealed record TranscriptDelta(string Text, TranscriptSource Source) : VoiceEvent;
    public sealed record TranscriptDone(string Text, TranscriptSource Source) : VoiceEvent;
    public sealed record Error(string Message) : VoiceEvent;
}
```

**Step 4: Implement parser** `OpenAgent.App.Core/Voice/VoiceEventParser.cs`:

```csharp
using System.Text.Json;

namespace OpenAgent.App.Core.Voice;

public static class VoiceEventParser
{
    public static VoiceEvent? Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("type", out var typeProp)) return null;
        var type = typeProp.GetString();
        var root = doc.RootElement;

        return type switch
        {
            "session_ready" => new VoiceEvent.SessionReady(
                root.GetProperty("input_sample_rate").GetInt32(),
                root.GetProperty("output_sample_rate").GetInt32(),
                root.GetProperty("input_codec").GetString() ?? "",
                root.GetProperty("output_codec").GetString() ?? ""),
            "speech_started" => new VoiceEvent.SpeechStarted(),
            "speech_stopped" => new VoiceEvent.SpeechStopped(),
            "audio_done" => new VoiceEvent.AudioDone(),
            "thinking_started" => new VoiceEvent.ThinkingStarted(),
            "thinking_stopped" => new VoiceEvent.ThinkingStopped(),
            "transcript_delta" => new VoiceEvent.TranscriptDelta(
                root.GetProperty("text").GetString() ?? "",
                ParseSource(root.GetProperty("source").GetString())),
            "transcript_done" => new VoiceEvent.TranscriptDone(
                root.GetProperty("text").GetString() ?? "",
                ParseSource(root.GetProperty("source").GetString())),
            "error" => new VoiceEvent.Error(root.GetProperty("message").GetString() ?? ""),
            _ => null
        };
    }

    private static TranscriptSource ParseSource(string? s) =>
        string.Equals(s, "user", StringComparison.OrdinalIgnoreCase)
            ? TranscriptSource.User
            : TranscriptSource.Assistant;
}
```

**Step 5: Run, expect PASS.**

**Step 6: Commit.**

```bash
git commit -am "feat(app-core): VoiceEvent union + parser"
```

> Note: the agent's `WebSocketVoiceEndpoints.cs` emits `VoiceThinkingStartedEvent` and `VoiceThinkingStoppedEvent`. Verify the actual JSON `type` strings emitted by those classes (look at `OpenAgent.Models.Voice.VoiceThinkingStartedEvent`). Adjust the parser's strings if they differ from `thinking_started` / `thinking_stopped`.

---

### Task 6: Reconnect backoff schedule

**Files:**
- Create: `src/app/OpenAgent.App.Core/Voice/ReconnectBackoff.cs`
- Test: `src/app/OpenAgent.App.Tests/Voice/ReconnectBackoffTests.cs`

**Step 1: Test.**

```csharp
using OpenAgent.App.Core.Voice;

namespace OpenAgent.App.Tests.Voice;

public class ReconnectBackoffTests
{
    [Fact]
    public void Schedule_is_1_2_4_8s_then_gives_up()
    {
        var b = new ReconnectBackoff(maxTries: 5);
        Assert.Equal(TimeSpan.FromSeconds(1), b.NextDelay());
        Assert.Equal(TimeSpan.FromSeconds(2), b.NextDelay());
        Assert.Equal(TimeSpan.FromSeconds(4), b.NextDelay());
        Assert.Equal(TimeSpan.FromSeconds(8), b.NextDelay());
        Assert.Equal(TimeSpan.FromSeconds(8), b.NextDelay()); // capped
        Assert.True(b.GiveUp);
    }

    [Fact]
    public void Reset_resets_attempt_count()
    {
        var b = new ReconnectBackoff(maxTries: 3);
        b.NextDelay(); b.NextDelay(); b.NextDelay();
        Assert.True(b.GiveUp);
        b.Reset();
        Assert.False(b.GiveUp);
    }
}
```

**Step 2: Run, expect FAIL.**

**Step 3: Implement.**

```csharp
namespace OpenAgent.App.Core.Voice;

/// <summary>Exponential backoff with a fixed cap. Attempts after maxTries set GiveUp=true.</summary>
public sealed class ReconnectBackoff
{
    private readonly int _maxTries;
    private int _attempt;

    public ReconnectBackoff(int maxTries = 5) => _maxTries = maxTries;

    public bool GiveUp => _attempt >= _maxTries;

    public TimeSpan NextDelay()
    {
        if (_attempt < _maxTries) _attempt++;
        var seconds = Math.Min(8, 1 << Math.Min(3, _attempt - 1));
        return TimeSpan.FromSeconds(seconds);
    }

    public void Reset() => _attempt = 0;
}
```

**Step 4: Run, expect PASS.**

**Step 5: Commit.**

```bash
git commit -am "feat(app-core): ReconnectBackoff"
```

---

### Task 7: Transcript router (source-flip rule)

**Files:**
- Create: `src/app/OpenAgent.App.Core/Voice/TranscriptRouter.cs`
- Test: `src/app/OpenAgent.App.Tests/Voice/TranscriptRouterTests.cs`

**Step 1: Test.** Mirrors `useVoiceSession.ts:88-117`:

```csharp
using OpenAgent.App.Core.Voice;

namespace OpenAgent.App.Tests.Voice;

public class TranscriptRouterTests
{
    private readonly List<(TranscriptSource src, string content)> _events = new();
    private TranscriptRouter Make() => new(
        onAppend: (src, text) => _events.Add(("APPEND_" + src, text)),
        onUpdateLast: (text) => _events.Add(("UPDATE", text))
    );

    [Fact]
    public void First_delta_appends()
    {
        var r = Make();
        r.OnDelta(TranscriptSource.User, "hi");
        Assert.Single(_events);
        Assert.Equal("APPEND_User", _events[0].src.ToString());
        Assert.Equal("hi", _events[0].content);
    }

    [Fact]
    public void Same_source_grows_via_update()
    {
        var r = Make();
        r.OnDelta(TranscriptSource.User, "hi");
        r.OnDelta(TranscriptSource.User, " there");
        Assert.Equal(2, _events.Count);
        Assert.Equal("UPDATE", _events[1].src.ToString());
        Assert.Equal("hi there", _events[1].content);
    }

    [Fact]
    public void Source_flip_appends_new_bubble()
    {
        var r = Make();
        r.OnDelta(TranscriptSource.User, "hi");
        r.OnDelta(TranscriptSource.Assistant, "yo");
        Assert.Equal(2, _events.Count);
        Assert.Equal("APPEND_Assistant", _events[1].src.ToString());
    }

    [Fact]
    public void Done_resets_so_next_delta_appends()
    {
        var r = Make();
        r.OnDelta(TranscriptSource.User, "hi");
        r.OnDone();
        r.OnDelta(TranscriptSource.User, "again");
        Assert.Equal(2, _events.Count);
        Assert.Equal("APPEND_User", _events[1].src.ToString());
    }
}
```

> Helper: the test's tuple type uses `TranscriptSource` for the first element only when source is real; the marker strings (`UPDATE`, `APPEND_User`) are abused into that field for assertion brevity. Replace with a discriminated record if it gets ugly.

**Step 2: Run, expect FAIL.**

**Step 3: Implement.**

```csharp
namespace OpenAgent.App.Core.Voice;

/// <summary>Routes transcript_delta events into either "append a new bubble" or "grow the last bubble"
/// per the source-flip rule. Mirrors src/web/src/apps/chat/hooks/useVoiceSession.ts.</summary>
public sealed class TranscriptRouter
{
    private readonly Action<TranscriptSource, string> _onAppend;
    private readonly Action<string> _onUpdateLast;
    private TranscriptSource? _lastSource;
    private string _accumulated = "";

    public TranscriptRouter(Action<TranscriptSource, string> onAppend, Action<string> onUpdateLast)
    {
        _onAppend = onAppend;
        _onUpdateLast = onUpdateLast;
    }

    public void OnDelta(TranscriptSource source, string text)
    {
        if (_lastSource != source)
        {
            _lastSource = source;
            _accumulated = text;
            _onAppend(source, text);
        }
        else
        {
            _accumulated += text;
            _onUpdateLast(_accumulated);
        }
    }

    public void OnDone()
    {
        _lastSource = null;
        _accumulated = "";
    }
}
```

**Step 4: Adjust the test's tuple-string assertions** if needed so they compile cleanly (use a string for both fields). Then run, expect PASS.

**Step 5: Commit.**

```bash
git commit -am "feat(app-core): TranscriptRouter source-flip"
```

---

### Task 8: Call state machine

**Files:**
- Create: `src/app/OpenAgent.App.Core/Voice/CallState.cs`
- Create: `src/app/OpenAgent.App.Core/Voice/CallStateMachine.cs`
- Test: `src/app/OpenAgent.App.Tests/Voice/CallStateMachineTests.cs`

**Step 1: Test all transitions.**

```csharp
using OpenAgent.App.Core.Voice;

namespace OpenAgent.App.Tests.Voice;

public class CallStateMachineTests
{
    [Fact]
    public void Starts_idle()
        => Assert.Equal(CallState.Idle, new CallStateMachine().State);

    [Fact]
    public void Connecting_to_listening_via_session_ready()
    {
        var sm = new CallStateMachine();
        sm.OnConnecting();
        Assert.Equal(CallState.Connecting, sm.State);
        sm.Apply(new VoiceEvent.SessionReady(24000, 24000, "pcm16", "pcm16"));
        Assert.Equal(CallState.Listening, sm.State);
    }

    [Fact]
    public void Speech_started_means_user_speaking()
    {
        var sm = new CallStateMachine();
        sm.OnConnecting();
        sm.Apply(new VoiceEvent.SessionReady(24000, 24000, "pcm16", "pcm16"));
        sm.Apply(new VoiceEvent.SpeechStarted());
        Assert.Equal(CallState.UserSpeaking, sm.State);
    }

    [Fact]
    public void Speech_stopped_then_audio_done_listens_again()
    {
        var sm = Bootstrap();
        sm.Apply(new VoiceEvent.SpeechStarted());
        sm.Apply(new VoiceEvent.SpeechStopped());
        Assert.Equal(CallState.Thinking, sm.State);
        sm.Apply(new VoiceEvent.AudioDone());
        Assert.Equal(CallState.Listening, sm.State);
    }

    [Fact]
    public void Audio_received_marks_assistant_speaking()
    {
        var sm = Bootstrap();
        sm.OnAudioReceived();
        Assert.Equal(CallState.AssistantSpeaking, sm.State);
    }

    [Fact]
    public void Disconnect_marks_reconnecting()
    {
        var sm = Bootstrap();
        sm.OnReconnecting();
        Assert.Equal(CallState.Reconnecting, sm.State);
    }

    private static CallStateMachine Bootstrap()
    {
        var sm = new CallStateMachine();
        sm.OnConnecting();
        sm.Apply(new VoiceEvent.SessionReady(24000, 24000, "pcm16", "pcm16"));
        return sm;
    }
}
```

**Step 2: Run, expect FAIL.**

**Step 3: Implement.**

`OpenAgent.App.Core/Voice/CallState.cs`:

```csharp
namespace OpenAgent.App.Core.Voice;

public enum CallState { Idle, Connecting, Listening, UserSpeaking, Thinking, AssistantSpeaking, Reconnecting, Ended }
```

`OpenAgent.App.Core/Voice/CallStateMachine.cs`:

```csharp
namespace OpenAgent.App.Core.Voice;

/// <summary>Pure state machine for the call screen — no I/O, fully unit-tested.</summary>
public sealed class CallStateMachine
{
    public CallState State { get; private set; } = CallState.Idle;

    public void OnConnecting() => State = CallState.Connecting;
    public void OnReconnecting() => State = CallState.Reconnecting;
    public void OnEnded() => State = CallState.Ended;

    public void OnAudioReceived() => State = CallState.AssistantSpeaking;

    public void Apply(VoiceEvent evt)
    {
        switch (evt)
        {
            case VoiceEvent.SessionReady: State = CallState.Listening; break;
            case VoiceEvent.SpeechStarted: State = CallState.UserSpeaking; break;
            case VoiceEvent.SpeechStopped: State = CallState.Thinking; break;
            case VoiceEvent.AudioDone: State = CallState.Listening; break;
        }
    }
}
```

**Step 4: Run, expect PASS.**

**Step 5: Commit.**

```bash
git commit -am "feat(app-core): CallStateMachine"
```

---

### Task 9: Conversation cache (filesystem JSON)

**Files:**
- Create: `src/app/OpenAgent.App.Core/Services/ConversationCache.cs`
- Test: `src/app/OpenAgent.App.Tests/Services/ConversationCacheTests.cs`

**Step 1: Test.**

```csharp
using OpenAgent.App.Core.Models;
using OpenAgent.App.Core.Services;

namespace OpenAgent.App.Tests.Services;

public class ConversationCacheTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "ccache_" + Guid.NewGuid().ToString("N"));

    public void Dispose() { try { Directory.Delete(_tmp, recursive: true); } catch { } }

    [Fact]
    public async Task Round_trips_list()
    {
        var c = new ConversationCache(_tmp);
        var items = new List<ConversationListItem>
        {
            new() { Id = "a", Source = "app", CreatedAt = DateTimeOffset.UtcNow }
        };
        await c.WriteAsync(items);
        var got = await c.ReadAsync();
        Assert.Single(got!);
        Assert.Equal("a", got![0].Id);
    }

    [Fact]
    public async Task Read_when_missing_returns_null()
    {
        var c = new ConversationCache(_tmp);
        Assert.Null(await c.ReadAsync());
    }

    [Fact]
    public async Task Read_corrupted_returns_null_and_does_not_throw()
    {
        Directory.CreateDirectory(_tmp);
        await File.WriteAllTextAsync(Path.Combine(_tmp, "conversations.cache.json"), "{not json");
        var c = new ConversationCache(_tmp);
        Assert.Null(await c.ReadAsync());
    }
}
```

**Step 2: Run, expect FAIL.**

**Step 3: Implement.**

```csharp
using System.Text.Json;
using OpenAgent.App.Core.Models;

namespace OpenAgent.App.Core.Services;

public sealed class ConversationCache
{
    private readonly string _path;

    public ConversationCache(string baseDirectory)
    {
        _path = Path.Combine(baseDirectory, "conversations.cache.json");
    }

    public async Task<List<ConversationListItem>?> ReadAsync(CancellationToken ct = default)
    {
        try
        {
            if (!File.Exists(_path)) return null;
            await using var stream = File.OpenRead(_path);
            return await JsonSerializer.DeserializeAsync<List<ConversationListItem>>(stream, JsonOptions.Default, ct);
        }
        catch
        {
            return null;
        }
    }

    public async Task WriteAsync(IReadOnlyList<ConversationListItem> items, CancellationToken ct = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var tmp = _path + ".tmp";
        await using (var stream = File.Create(tmp))
        {
            await JsonSerializer.SerializeAsync(stream, items, JsonOptions.Default, ct);
        }
        File.Move(tmp, _path, overwrite: true);
    }
}
```

**Step 4: Run, expect PASS.**

**Step 5: Commit.**

```bash
git commit -am "feat(app-core): ConversationCache"
```

---

### Task 10: ApiClient (REST) with injectable HttpMessageHandler

**Files:**
- Create: `src/app/OpenAgent.App.Core/Api/ApiClient.cs`
- Create: `src/app/OpenAgent.App.Core/Api/IApiClient.cs`
- Create: `src/app/OpenAgent.App.Core/Api/ApiException.cs`
- Test: `src/app/OpenAgent.App.Tests/Api/ApiClientTests.cs`
- Test helper: `src/app/OpenAgent.App.Tests/Api/StubHandler.cs`

**Step 1: Tests** covering GET list, DELETE, PATCH, 401 handling, network failure:

```csharp
using System.Net;
using OpenAgent.App.Core.Api;
using OpenAgent.App.Core.Onboarding;
using OpenAgent.App.Core.Services;

namespace OpenAgent.App.Tests.Api;

public class ApiClientTests
{
    private (ApiClient client, StubHandler stub) Make(
        Func<HttpRequestMessage, HttpResponseMessage>? respond = null)
    {
        var store = new InMemoryCredentialStore();
        store.SaveAsync(new QrPayload("https://agent.example/", "tok123")).GetAwaiter().GetResult();
        var stub = new StubHandler(respond ?? (_ => new HttpResponseMessage(HttpStatusCode.OK)));
        var http = new HttpClient(stub);
        return (new ApiClient(http, store), stub);
    }

    [Fact]
    public async Task Get_conversations_sends_api_key_and_parses()
    {
        var (client, stub) = Make(req =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""[{"id":"x","source":"app","text_provider":"o","text_model":"m","voice_provider":"o","voice_model":"m","created_at":"2026-04-29T10:00:00Z","total_prompt_tokens":0,"total_completion_tokens":0,"turn_count":0}]""")
            });
        var items = await client.GetConversationsAsync();
        Assert.Single(items);
        Assert.Equal("https://agent.example/api/conversations", stub.LastRequest!.RequestUri!.ToString());
        Assert.Equal("tok123", stub.LastRequest.Headers.GetValues("X-Api-Key").Single());
    }

    [Fact]
    public async Task Delete_returns_when_204()
    {
        var (client, _) = Make(_ => new HttpResponseMessage(HttpStatusCode.NoContent));
        await client.DeleteConversationAsync("x");
    }

    [Fact]
    public async Task Patch_intention_sends_json_body()
    {
        string? body = null;
        var (client, _) = Make(req =>
        {
            body = req.Content!.ReadAsStringAsync().Result;
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        await client.RenameConversationAsync("x", "Hello world");
        Assert.Contains("\"intention\":\"Hello world\"", body);
    }

    [Fact]
    public async Task Throws_AuthRejected_on_401()
    {
        var (client, _) = Make(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));
        await Assert.ThrowsAsync<AuthRejectedException>(() => client.GetConversationsAsync());
    }
}
```

`StubHandler.cs`:

```csharp
namespace OpenAgent.App.Tests.Api;

internal sealed class StubHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;
    public HttpRequestMessage? LastRequest { get; private set; }

    public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        LastRequest = request;
        return Task.FromResult(_respond(request));
    }
}
```

**Step 2: Run, expect FAIL.**

**Step 3: Implement** `OpenAgent.App.Core/Api/ApiException.cs`:

```csharp
namespace OpenAgent.App.Core.Api;

public sealed class ApiException(string message, int statusCode) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}

public sealed class AuthRejectedException() : Exception("API key rejected by agent");

public sealed class NetworkException(string message, Exception inner) : Exception(message, inner);
```

`OpenAgent.App.Core/Api/IApiClient.cs`:

```csharp
using OpenAgent.App.Core.Models;

namespace OpenAgent.App.Core.Api;

public interface IApiClient
{
    Task<List<ConversationListItem>> GetConversationsAsync(CancellationToken ct = default);
    Task DeleteConversationAsync(string conversationId, CancellationToken ct = default);
    Task RenameConversationAsync(string conversationId, string intention, CancellationToken ct = default);
}
```

`OpenAgent.App.Core/Api/ApiClient.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using OpenAgent.App.Core.Models;
using OpenAgent.App.Core.Services;

namespace OpenAgent.App.Core.Api;

/// <summary>HTTP client to the agent's REST endpoints. Reads BaseUrl + Token from ICredentialStore on every call.</summary>
public sealed class ApiClient : IApiClient
{
    private readonly HttpClient _http;
    private readonly ICredentialStore _credentials;

    public ApiClient(HttpClient http, ICredentialStore credentials)
    {
        _http = http;
        _credentials = credentials;
    }

    public Task<List<ConversationListItem>> GetConversationsAsync(CancellationToken ct = default)
        => SendAsync<List<ConversationListItem>>(HttpMethod.Get, "api/conversations", null, ct)!;

    public Task DeleteConversationAsync(string conversationId, CancellationToken ct = default)
        => SendAsync<object>(HttpMethod.Delete, $"api/conversations/{Uri.EscapeDataString(conversationId)}", null, ct);

    public Task RenameConversationAsync(string conversationId, string intention, CancellationToken ct = default)
    {
        var body = JsonContent.Create(new { intention }, options: JsonOptions.Default);
        return SendAsync<object>(HttpMethod.Patch, $"api/conversations/{Uri.EscapeDataString(conversationId)}", body, ct);
    }

    private async Task<T?> SendAsync<T>(HttpMethod method, string path, HttpContent? body, CancellationToken ct)
    {
        var creds = await _credentials.LoadAsync(ct) ?? throw new InvalidOperationException("No credentials");
        var req = new HttpRequestMessage(method, new Uri(new Uri(creds.BaseUrl), path));
        req.Headers.Add("X-Api-Key", creds.Token);
        if (body is not null) req.Content = body;

        HttpResponseMessage resp;
        try { resp = await _http.SendAsync(req, ct); }
        catch (Exception ex) { throw new NetworkException(ex.Message, ex); }

        if (resp.StatusCode == HttpStatusCode.Unauthorized) throw new AuthRejectedException();

        if (!resp.IsSuccessStatusCode)
        {
            var content = await resp.Content.ReadAsStringAsync(ct);
            throw new ApiException($"{(int)resp.StatusCode} {resp.ReasonPhrase}: {content}", (int)resp.StatusCode);
        }

        if (typeof(T) == typeof(object)) return default;
        return await resp.Content.ReadFromJsonAsync<T>(JsonOptions.Default, ct);
    }
}
```

**Step 4: Run, expect PASS.**

**Step 5: Commit.**

```bash
git commit -am "feat(app-core): ApiClient with X-Api-Key auth"
```

> Verify the actual `PATCH` body shape against `ConversationEndpoints.cs` in the agent. Confirm: field name is `intention`, omitted-keeps-current is implemented server-side. If renaming uses a different field name, adjust the `RenameConversationAsync` body and add a test.

---

### Task 11: VoiceWebSocketClient

**Files:**
- Create: `src/app/OpenAgent.App.Core/Voice/IVoiceWebSocketClient.cs`
- Create: `src/app/OpenAgent.App.Core/Voice/VoiceWebSocketClient.cs`
- Create: `src/app/OpenAgent.App.Core/Voice/VoiceFrame.cs`

**Note: this task is mostly integration glue and is hard to unit-test in isolation.** Strategy: scaffold with a thin interface so the call view-model can be tested with a fake; real behaviour is verified end-to-end on a TestFlight build.

**Step 1: Define the frame envelope.** `OpenAgent.App.Core/Voice/VoiceFrame.cs`:

```csharp
namespace OpenAgent.App.Core.Voice;

/// <summary>One chunk emitted by the receive loop — either a typed event or a raw audio buffer.</summary>
public abstract record VoiceFrame
{
    public sealed record EventFrame(VoiceEvent Event) : VoiceFrame;
    public sealed record AudioFrame(byte[] Pcm16) : VoiceFrame;
    public sealed record Disconnected(string? Reason, bool AuthRejected) : VoiceFrame;
}
```

**Step 2: Define the interface** `OpenAgent.App.Core/Voice/IVoiceWebSocketClient.cs`:

```csharp
namespace OpenAgent.App.Core.Voice;

public interface IVoiceWebSocketClient : IAsyncDisposable
{
    Task ConnectAsync(string conversationId, CancellationToken ct);
    Task SendAudioAsync(ReadOnlyMemory<byte> pcm16, CancellationToken ct);
    IAsyncEnumerable<VoiceFrame> ReadFramesAsync(CancellationToken ct);
}
```

**Step 3: Implement** `OpenAgent.App.Core/Voice/VoiceWebSocketClient.cs`:

```csharp
using System.Net.WebSockets;
using System.Text;
using OpenAgent.App.Core.Services;

namespace OpenAgent.App.Core.Voice;

public sealed class VoiceWebSocketClient : IVoiceWebSocketClient
{
    private readonly ICredentialStore _credentials;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private ClientWebSocket? _ws;

    public VoiceWebSocketClient(ICredentialStore credentials) => _credentials = credentials;

    public async Task ConnectAsync(string conversationId, CancellationToken ct)
    {
        var creds = await _credentials.LoadAsync(ct) ?? throw new InvalidOperationException("No credentials");
        var baseUri = new Uri(creds.BaseUrl);
        var scheme = baseUri.Scheme == "https" ? "wss" : "ws";
        var wsUrl = new UriBuilder($"{scheme}://{baseUri.Authority}{baseUri.AbsolutePath.TrimEnd('/')}/ws/conversations/{Uri.EscapeDataString(conversationId)}/voice")
        {
            Query = $"api_key={Uri.EscapeDataString(creds.Token)}"
        }.Uri;

        _ws = new ClientWebSocket();
        await _ws.ConnectAsync(wsUrl, ct);
    }

    /// <summary>
    /// Sends one PCM16 audio chunk. Must serialize sends — ClientWebSocket throws
    /// InvalidOperationException on concurrent SendAsync calls, and the audio capture tap
    /// can fire frequently from a render thread.
    /// </summary>
    public async Task SendAudioAsync(ReadOnlyMemory<byte> pcm16, CancellationToken ct)
    {
        var ws = _ws;
        if (ws is null || ws.State != WebSocketState.Open) return;
        await _sendLock.WaitAsync(ct);
        try
        {
            if (ws.State != WebSocketState.Open) return;
            await ws.SendAsync(pcm16, WebSocketMessageType.Binary, true, ct);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    public async IAsyncEnumerable<VoiceFrame> ReadFramesAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        if (_ws is null) yield break;
        var buffer = new byte[16 * 1024];
        var assembly = new MemoryStream();

        while (_ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            WebSocketReceiveResult result;
            try { result = await _ws.ReceiveAsync(buffer, ct); }
            catch (WebSocketException ex)
            {
                yield return new VoiceFrame.Disconnected(ex.Message, AuthRejected: false);
                yield break;
            }

            if (result.MessageType == WebSocketMessageType.Close)
            {
                var auth = _ws.CloseStatus is (WebSocketCloseStatus)1008 or (WebSocketCloseStatus)4001;
                yield return new VoiceFrame.Disconnected(_ws.CloseStatusDescription, auth);
                yield break;
            }

            assembly.Write(buffer, 0, result.Count);
            if (!result.EndOfMessage) continue;

            var bytes = assembly.ToArray();
            assembly.SetLength(0);

            if (result.MessageType == WebSocketMessageType.Binary)
            {
                yield return new VoiceFrame.AudioFrame(bytes);
            }
            else
            {
                var json = Encoding.UTF8.GetString(bytes);
                var evt = VoiceEventParser.Parse(json);
                if (evt is not null) yield return new VoiceFrame.EventFrame(evt);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        var ws = _ws;
        _ws = null;
        if (ws is null) return;
        try
        {
            if (ws.State is WebSocketState.Open or WebSocketState.CloseReceived)
                await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
        }
        catch { }
        ws.Dispose();
        _sendLock.Dispose();
    }
}
```

**Step 4: Build to confirm it compiles.**

Run: `cd src/app && dotnet build`
Expected: PASS.

**Step 5: Commit.**

```bash
git commit -am "feat(app-core): VoiceWebSocketClient"
```

---

## Phase 2 — MAUI app scaffolding

### Task 12: Create the MAUI iOS head project

**Files:**
- Create: `src/app/OpenAgent.App/OpenAgent.App.csproj`
- Create: `src/app/OpenAgent.App/MauiProgram.cs`
- Create: `src/app/OpenAgent.App/App.xaml` + `.xaml.cs`
- Create: `src/app/OpenAgent.App/AppShell.xaml` + `.xaml.cs`
- Create: `src/app/OpenAgent.App/Platforms/iOS/AppDelegate.cs`
- Create: `src/app/OpenAgent.App/Platforms/iOS/Program.cs`
- Create: `src/app/OpenAgent.App/Platforms/iOS/Info.plist`
- Create: `src/app/OpenAgent.App/Resources/AppIcon/appicon.svg` + `appiconfg.svg` (placeholder)
- Create: `src/app/OpenAgent.App/Resources/Splash/splash.svg` (placeholder)
- Create: `src/app/OpenAgent.App/Resources/Styles/Colors.xaml`
- Create: `src/app/OpenAgent.App/Resources/Styles/Styles.xaml`

**Step 1: csproj.**

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFrameworks>net10.0-ios</TargetFrameworks>
    <OutputType>Exe</OutputType>
    <RootNamespace>OpenAgent.App</RootNamespace>
    <UseMaui>true</UseMaui>
    <SingleProject>true</SingleProject>
    <ApplicationTitle>OpenAgent</ApplicationTitle>
    <ApplicationId>dk.muneris.openagent</ApplicationId>
    <ApplicationDisplayVersion>1.0</ApplicationDisplayVersion>
    <ApplicationVersion>1</ApplicationVersion>
    <SupportedOSPlatformVersion>17.0</SupportedOSPlatformVersion>
    <RuntimeIdentifier Condition="$(Configuration) == 'Release'">ios-arm64</RuntimeIdentifier>
  </PropertyGroup>

  <ItemGroup>
    <MauiIcon Include="Resources\AppIcon\appicon.svg" ForegroundFile="Resources\AppIcon\appiconfg.svg" Color="#0A84FF" />
    <MauiSplashScreen Include="Resources\Splash\splash.svg" Color="#000000" BaseSize="128,128" />
    <MauiXaml Update="**\*.xaml" SubType="Designer" />
    <MauiCss Include="**\*.css" />
  </ItemGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.Maui.Controls" />
    <PackageReference Include="Microsoft.Maui.Controls.Compatibility" />
    <PackageReference Include="Microsoft.Extensions.Logging.Debug" />
    <PackageReference Include="CommunityToolkit.Mvvm" />
    <PackageReference Include="ZXing.Net.Maui" />
    <PackageReference Include="ZXing.Net.Maui.Controls" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\OpenAgent.App.Core\OpenAgent.App.Core.csproj" />
  </ItemGroup>
</Project>
```

**Step 2: MauiProgram.cs.**

```csharp
using CommunityToolkit.Maui;
using Microsoft.Extensions.Logging;
using OpenAgent.App.Core.Api;
using OpenAgent.App.Core.Services;
using OpenAgent.App.Core.Voice;
using ZXing.Net.Maui.Controls;

namespace OpenAgent.App;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder.UseMauiApp<App>().UseBarcodeReader();

        // Core — credential store and HTTP client are singletons; the WS client owns one ClientWebSocket
        // per call so it must be transient (one fresh instance per CallViewModel resolution).
        builder.Services.AddSingleton<ICredentialStore, IosKeychainCredentialStore>();
        builder.Services.AddHttpClient<IApiClient, ApiClient>();
        builder.Services.AddTransient<IVoiceWebSocketClient, VoiceWebSocketClient>();
        builder.Services.AddSingleton(sp => new ConversationCache(FileSystem.AppDataDirectory));

        // ViewModels + Pages
        builder.Services.AddTransient<Pages.OnboardingPage>();
        builder.Services.AddTransient<ViewModels.OnboardingViewModel>();
        builder.Services.AddTransient<Pages.ManualEntryPage>();
        builder.Services.AddTransient<ViewModels.ManualEntryViewModel>();
        builder.Services.AddTransient<Pages.ConversationsPage>();
        builder.Services.AddTransient<ViewModels.ConversationsViewModel>();
        builder.Services.AddTransient<Pages.CallPage>();
        builder.Services.AddTransient<ViewModels.CallViewModel>();
        builder.Services.AddTransient<Pages.SettingsPage>();
        builder.Services.AddTransient<ViewModels.SettingsViewModel>();

#if DEBUG
        builder.Logging.AddDebug();
#endif
        return builder.Build();
    }
}
```

**Step 3: App.xaml + AppShell.xaml + Platforms/iOS — boilerplate.**

`App.xaml`:

```xml
<?xml version="1.0" encoding="UTF-8" ?>
<Application xmlns="http://schemas.microsoft.com/dotnet/2021/maui"
             xmlns:x="http://schemas.microsoft.com/winfx/2009/xaml"
             x:Class="OpenAgent.App.App">
  <Application.Resources>
    <ResourceDictionary>
      <ResourceDictionary.MergedDictionaries>
        <ResourceDictionary Source="Resources/Styles/Colors.xaml" />
        <ResourceDictionary Source="Resources/Styles/Styles.xaml" />
      </ResourceDictionary.MergedDictionaries>
    </ResourceDictionary>
  </Application.Resources>
</Application>
```

`App.xaml.cs`:

```csharp
namespace OpenAgent.App;

public partial class App : Application
{
    public App() { InitializeComponent(); MainPage = new AppShell(); }
}
```

`AppShell.xaml`:

```xml
<?xml version="1.0" encoding="UTF-8" ?>
<Shell xmlns="http://schemas.microsoft.com/dotnet/2021/maui"
       xmlns:x="http://schemas.microsoft.com/winfx/2009/xaml"
       xmlns:pages="clr-namespace:OpenAgent.App.Pages"
       x:Class="OpenAgent.App.AppShell"
       Shell.FlyoutBehavior="Disabled">
  <ShellContent ContentTemplate="{DataTemplate pages:OnboardingPage}" Route="onboarding" />
  <ShellContent ContentTemplate="{DataTemplate pages:ConversationsPage}" Route="conversations" />
</Shell>
```

`AppShell.xaml.cs`:

```csharp
using OpenAgent.App.Core.Services;

namespace OpenAgent.App;

public partial class AppShell : Shell
{
    public AppShell()
    {
        InitializeComponent();
        Routing.RegisterRoute("call", typeof(Pages.CallPage));
        Routing.RegisterRoute("settings", typeof(Pages.SettingsPage));
        Routing.RegisterRoute("manual-entry", typeof(Pages.ManualEntryPage));
    }

    /// <summary>Routes the user to onboarding or the conversations list based on stored credentials.
    /// Called from App.xaml.cs after MainPage is set; uses IPlatformApplication.Current.Services
    /// rather than this.Handler.MauiContext (which can be null when Loaded fires).</summary>
    public async Task RouteInitialAsync()
    {
        var services = IPlatformApplication.Current?.Services
                       ?? throw new InvalidOperationException("Service provider not available");
        var store = services.GetRequiredService<ICredentialStore>();
        var creds = await store.LoadAsync();
        await GoToAsync(creds is null ? "//onboarding" : "//conversations");
    }
}
```

Update `App.xaml.cs` to invoke routing after MainPage is set:

```csharp
namespace OpenAgent.App;

public partial class App : Application
{
    public App()
    {
        InitializeComponent();
        var shell = new AppShell();
        MainPage = shell;
        Dispatcher.Dispatch(async () => await shell.RouteInitialAsync());
    }
}
```

`Platforms/iOS/AppDelegate.cs`:

```csharp
using Foundation;
namespace OpenAgent.App;

[Register("AppDelegate")]
public class AppDelegate : MauiUIApplicationDelegate
{
    protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();
}
```

`Platforms/iOS/Program.cs`:

```csharp
using ObjCRuntime;
using UIKit;

namespace OpenAgent.App;

public class Program
{
    static void Main(string[] args) => UIApplication.Main(args, null, typeof(AppDelegate));
}
```

`Platforms/iOS/Info.plist`:

```xml
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleDisplayName</key>
  <string>OpenAgent</string>
  <key>CFBundleIdentifier</key>
  <string>dk.muneris.openagent</string>
  <key>CFBundleShortVersionString</key>
  <string>1.0</string>
  <key>CFBundleVersion</key>
  <string>1</string>
  <key>LSRequiresIPhoneOS</key>
  <true/>
  <key>MinimumOSVersion</key>
  <string>17.0</string>
  <key>UIDeviceFamily</key>
  <array><integer>1</integer></array>
  <key>UISupportedInterfaceOrientations</key>
  <array><string>UIInterfaceOrientationPortrait</string></array>
  <key>UIBackgroundModes</key>
  <array><string>audio</string></array>
  <key>NSMicrophoneUsageDescription</key>
  <string>OpenAgent needs the microphone to talk to the agent.</string>
  <key>NSCameraUsageDescription</key>
  <string>OpenAgent uses the camera to scan the connection QR code.</string>
</dict>
</plist>
```

**Step 4: Placeholder appicon + splash SVG.** Use a minimal blue circle for now — replaced before TestFlight.

`Resources/AppIcon/appicon.svg`:

```xml
<?xml version="1.0" encoding="UTF-8"?>
<svg xmlns="http://www.w3.org/2000/svg" width="456" height="456" viewBox="0 0 456 456">
  <rect width="456" height="456" fill="#0A84FF" />
</svg>
```

`Resources/AppIcon/appiconfg.svg`:

```xml
<?xml version="1.0" encoding="UTF-8"?>
<svg xmlns="http://www.w3.org/2000/svg" width="456" height="456" viewBox="0 0 456 456">
  <circle cx="228" cy="228" r="100" fill="white" />
</svg>
```

`Resources/Splash/splash.svg`:

```xml
<?xml version="1.0" encoding="UTF-8"?>
<svg xmlns="http://www.w3.org/2000/svg" width="128" height="128" viewBox="0 0 128 128">
  <circle cx="64" cy="64" r="40" fill="#0A84FF" />
</svg>
```

**Step 5: Add to solution.**

```bash
cd src/app && dotnet sln add OpenAgent.App/OpenAgent.App.csproj
```

**Step 6: Verify Core still builds + tests pass.**

Run: `cd src/app && dotnet build OpenAgent.App.Core/OpenAgent.App.Core.csproj`
Run: `cd src/app && dotnet test OpenAgent.App.Tests/OpenAgent.App.Tests.csproj`

The MAUI head won't build on Windows without the MAUI workload; that's fine — CI will build it. Optionally:

```bash
dotnet workload install maui-ios   # may fail on Windows; ignore
```

**Step 7: Commit.**

```bash
git commit -am "feat(app): MAUI iOS head scaffolding"
```

---

## Phase 3 — iOS platform glue (untested from Windows)

### Task 13: iOS Keychain credential store

**Files:**
- Create: `src/app/OpenAgent.App/Platforms/iOS/IosKeychainCredentialStore.cs`

**Step 1: Implement.**

```csharp
using System.Text.Json;
using Foundation;
using OpenAgent.App.Core.Onboarding;
using OpenAgent.App.Core.Services;
using Security;

namespace OpenAgent.App;

/// <summary>Stores credentials as a JSON blob in the iOS Keychain. Service "OpenAgent", account "default".</summary>
public sealed class IosKeychainCredentialStore : ICredentialStore
{
    private const string Service = "OpenAgent";
    private const string Account = "default";

    public Task<QrPayload?> LoadAsync(CancellationToken ct = default)
    {
        var query = NewQuery();
        query.ReturnData = true;
        var result = SecKeyChain.QueryAsData(query, false, out var status);
        if (status != SecStatusCode.Success || result is null) return Task.FromResult<QrPayload?>(null);
        var json = result.ToString();
        var payload = JsonSerializer.Deserialize<QrPayload>(json);
        return Task.FromResult(payload);
    }

    public Task SaveAsync(QrPayload payload, CancellationToken ct = default)
    {
        var data = NSData.FromString(JsonSerializer.Serialize(payload));
        var query = NewQuery();
        var existing = SecKeyChain.QueryAsRecord(query, out _);
        if (existing is not null)
        {
            SecKeyChain.Update(query, new SecRecord(SecKind.GenericPassword) { ValueData = data });
        }
        else
        {
            var record = NewQuery();
            record.ValueData = data;
            SecKeyChain.Add(record);
        }
        return Task.CompletedTask;
    }

    public Task ClearAsync(CancellationToken ct = default)
    {
        SecKeyChain.Remove(NewQuery());
        return Task.CompletedTask;
    }

    private static SecRecord NewQuery() => new(SecKind.GenericPassword)
    {
        Service = Service,
        Account = Account
    };
}
```

**Step 2: Commit.** (Cannot build on Windows — verified by CI later.)

```bash
git commit -am "feat(app-ios): Keychain credential store"
```

---

### Task 14: iOS audio engine

**Files:**
- Create: `src/app/OpenAgent.App.Core/Voice/ICallAudio.cs`
- Create: `src/app/OpenAgent.App/Platforms/iOS/IosCallAudio.cs`

**Step 1: Interface in Core.**

```csharp
namespace OpenAgent.App.Core.Voice;

/// <summary>Platform-specific audio capture + playback for an active call. iOS impl uses AVAudioEngine.</summary>
public interface ICallAudio : IAsyncDisposable
{
    Task StartAsync(int sampleRate, CancellationToken ct);
    Task StopAsync();
    void EnqueuePlayback(byte[] pcm16);
    void FlushPlayback();
    void SetMuted(bool muted);
    event Action<byte[]>? OnPcmCaptured;
}
```

**Step 2: iOS implementation.**

`src/app/OpenAgent.App/Platforms/iOS/IosCallAudio.cs`:

```csharp
using AVFoundation;
using AudioToolbox;
using Foundation;
using OpenAgent.App.Core.Voice;

namespace OpenAgent.App;

public sealed class IosCallAudio : ICallAudio
{
    private AVAudioEngine? _engine;
    private AVAudioPlayerNode? _player;
    private AVAudioFormat? _outFormat;          // 16-bit, mono, target sample rate (e.g. 24 kHz)
    private AVAudioFormat? _inputNativeFormat;  // mic's natural format (varies by device)
    private AVAudioConverter? _converter;       // cached — allocating per buffer drops frames
    private bool _muted;
    private readonly object _lifecycleLock = new();

    public event Action<byte[]>? OnPcmCaptured;

    public async Task StartAsync(int sampleRate, CancellationToken ct)
    {
        var session = AVAudioSession.SharedInstance();
        session.SetCategory(AVAudioSessionCategory.PlayAndRecord,
            AVAudioSessionCategoryOptions.AllowBluetooth | AVAudioSessionCategoryOptions.DefaultToSpeaker);
        session.SetMode(AVAudioSession.ModeVoiceChat, out _);
        session.SetActive(true, out _);

        _outFormat = new AVAudioFormat(AVAudioCommonFormat.PCMInt16, sampleRate, 1, interleaved: true);

        _engine = new AVAudioEngine();
        _player = new AVAudioPlayerNode();
        _engine.AttachNode(_player);
        _engine.Connect(_player, _engine.MainMixerNode, _outFormat);

        var input = _engine.InputNode;
        _inputNativeFormat = input.GetBusOutputFormat(0);
        _converter = new AVAudioConverter(_inputNativeFormat, _outFormat);

        // Tap captures at the input node's native format; converter resamples to mono PCM16 at target rate.
        input.InstallTapOnBus(0, 4096, _inputNativeFormat, (buffer, _) =>
        {
            if (_muted) return;
            var bytes = ConvertToInt16Mono(buffer);
            if (bytes is { Length: > 0 }) OnPcmCaptured?.Invoke(bytes);
        });

        _engine.Prepare();
        _engine.StartAndReturnError(out var err);
        if (err is not null) throw new InvalidOperationException(err.LocalizedDescription);
        _player.Play();
        await Task.CompletedTask;
    }

    public Task StopAsync()
    {
        lock (_lifecycleLock)
        {
            _player?.Stop();
            try { _engine?.InputNode.RemoveTapOnBus(0); } catch { }
            _engine?.Stop();
            _converter?.Dispose();
            _player = null;
            _engine = null;
            _converter = null;
            _outFormat = null;
            _inputNativeFormat = null;
        }
        try { AVAudioSession.SharedInstance().SetActive(false, out _); } catch { }
        return Task.CompletedTask;
    }

    public void EnqueuePlayback(byte[] pcm16)
    {
        var player = _player;
        var format = _outFormat;
        if (player is null || format is null) return;

        var frameCount = (uint)(pcm16.Length / 2);
        if (frameCount == 0) return;

        var buffer = new AVAudioPcmBuffer(format, frameCount) { FrameLength = frameCount };

        unsafe
        {
            // Format is interleaved PCM16 mono; channel data 0 is the entire stream.
            var dst = (short*)buffer.Int16ChannelData[0];
            for (var i = 0; i < frameCount; i++)
                dst[i] = (short)(pcm16[i * 2] | (pcm16[i * 2 + 1] << 8));
        }

        player.ScheduleBuffer(buffer, () => { });
    }

    public void FlushPlayback()
    {
        // Drop anything queued so the assistant's reply is interrupted on user barge-in.
        _player?.Stop();
        _player?.Play();
    }

    public void SetMuted(bool muted) => _muted = muted;

    private byte[] ConvertToInt16Mono(AVAudioPcmBuffer src)
    {
        var converter = _converter;
        var outFormat = _outFormat;
        if (converter is null || outFormat is null) return Array.Empty<byte>();

        // Output capacity: src.FrameLength * (outRate / srcRate), rounded up + safety margin.
        var ratio = outFormat.SampleRate / src.Format.SampleRate;
        var frameCapacity = (uint)Math.Ceiling(src.FrameLength * ratio) + 64;
        var dst = new AVAudioPcmBuffer(outFormat, frameCapacity);

        var consumed = false;
        var outStatus = converter.ConvertToBuffer(dst, out var error,
            (AVAudioPacketCount _, out AVAudioConverterInputStatus s) =>
            {
                if (consumed) { s = AVAudioConverterInputStatus.NoDataNow; return null!; }
                consumed = true;
                s = AVAudioConverterInputStatus.HaveData;
                return src;
            });

        if (error is not null) return Array.Empty<byte>();
        if (outStatus is AVAudioConverterOutputStatus.Error) return Array.Empty<byte>();

        var byteCount = (int)(dst.FrameLength * 2);
        if (byteCount <= 0) return Array.Empty<byte>();
        var bytes = new byte[byteCount];

        unsafe
        {
            var srcPtr = (short*)dst.Int16ChannelData[0];
            for (var i = 0; i < dst.FrameLength; i++)
            {
                var s = srcPtr[i];
                bytes[i * 2]     = (byte)(s & 0xFF);          // little-endian PCM16
                bytes[i * 2 + 1] = (byte)((s >> 8) & 0xFF);
            }
        }
        return bytes;
    }

    public ValueTask DisposeAsync()
    {
        StopAsync().GetAwaiter().GetResult();
        return ValueTask.CompletedTask;
    }
}
```

**Step 3: Register in `MauiProgram.cs`.** Add inside `#if IOS`:

```csharp
#if IOS
        builder.Services.AddTransient<ICallAudio, IosCallAudio>();
#endif
```

(Transient — the call view-model owns one engine per call.)

**Step 4: Commit.**

```bash
git commit -am "feat(app-ios): AVAudioEngine capture + playback"
```

**TestFlight verification rubric (concrete acceptance criteria — replaces "fix if it sounds bad").**

This audio code can't be exercised on Windows. Acceptance for this task is met when, on a TestFlight build:

1. **Capture path.** Open the app, tap into a call. Speak the test phrase "one two three four five". Stop. Inspect the agent's stored `Messages` table: the user transcript line for that turn must contain at least three of the five spoken words (allowing for ASR misrecognition). Failure modes: empty transcript (tap is firing but converter is producing zeros), garbled transcript (sample-rate ratio off → speed/pitch wrong → agent hears nonsense).
2. **Playback path.** Listen for the agent reply. Acceptance: speech is intelligible — not stuttering, not pitch-shifted, not noticeably delayed beyond ~500ms first-audio. Failure modes: chopped audio (frame ordering broken), high-pitched chipmunk (sample rate mismatch on the player node), silence (player not connected to the mixer).
3. **Barge-in.** Start agent reply, then speak over it. Acceptance: agent audio cuts immediately on `speech_started`, your speech reaches the agent. Failure: agent keeps talking — `FlushPlayback` is broken.
4. **Background mode.** Start a call, lock the phone. Acceptance: audio continues, dynamic island shows recording indicator. Failure: audio cuts on lock — `UIBackgroundModes` not honored or session deactivated.
5. **Mute.** Tap mute mid-call, speak, unmute. Acceptance: agent doesn't react to muted speech, then resumes hearing on unmute. Failure: mute is no-op (the `_muted` flag isn't gating the tap).

Run all five before declaring v1 ready. Failures point at specific code: 1→tap/converter, 2→ScheduleBuffer/format, 3→FlushPlayback, 4→Info.plist + session category, 5→`SetMuted`.

---

## Phase 4 — UI

### Task 15: Onboarding pages (QR + manual)

**Files:**
- Create: `src/app/OpenAgent.App/Pages/OnboardingPage.xaml` + `.xaml.cs`
- Create: `src/app/OpenAgent.App/ViewModels/OnboardingViewModel.cs`
- Create: `src/app/OpenAgent.App/Pages/ManualEntryPage.xaml` + `.xaml.cs`
- Create: `src/app/OpenAgent.App/ViewModels/ManualEntryViewModel.cs`

**Step 1: OnboardingViewModel** — only knows about scanning a QR; the "Enter manually" button just navigates to a separate page.

```csharp
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenAgent.App.Core.Onboarding;
using OpenAgent.App.Core.Services;

namespace OpenAgent.App.ViewModels;

public partial class OnboardingViewModel : ObservableObject
{
    private readonly ICredentialStore _store;

    public OnboardingViewModel(ICredentialStore store) => _store = store;

    [ObservableProperty] private string? _error;
    [ObservableProperty] private bool _hasError;

    [RelayCommand]
    public async Task OnQrScannedAsync(string text)
    {
        if (!QrPayloadParser.TryParse(text, out var payload, out var err))
        {
            Error = err;
            HasError = true;
            return;
        }
        await _store.SaveAsync(payload!);
        await Shell.Current.GoToAsync("//conversations");
    }

    [RelayCommand]
    public Task OpenManualEntryAsync() => Shell.Current.GoToAsync("manual-entry");
}
```

**Step 2: ManualEntryViewModel.**

```csharp
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenAgent.App.Core.Onboarding;
using OpenAgent.App.Core.Services;

namespace OpenAgent.App.ViewModels;

public partial class ManualEntryViewModel : ObservableObject
{
    private readonly ICredentialStore _store;

    public ManualEntryViewModel(ICredentialStore store) => _store = store;

    [ObservableProperty] private string _serverUrl = "";
    [ObservableProperty] private string _token = "";
    [ObservableProperty] private string? _error;
    [ObservableProperty] private bool _hasError;

    [RelayCommand]
    public async Task SaveAsync()
    {
        var probe = $"{ServerUrl.TrimEnd('/')}/?token={Token}";
        if (!QrPayloadParser.TryParse(probe, out var payload, out var err))
        {
            Error = err;
            HasError = true;
            return;
        }
        await _store.SaveAsync(payload!);
        await Shell.Current.GoToAsync("//conversations");
    }
}
```

**Step 3: OnboardingPage.xaml.** Note: `IsVisible="{Binding HasError}"` uses a plain bool — no custom converter needed.

```xml
<?xml version="1.0" encoding="utf-8" ?>
<ContentPage xmlns="http://schemas.microsoft.com/dotnet/2021/maui"
             xmlns:x="http://schemas.microsoft.com/winfx/2009/xaml"
             xmlns:zxing="clr-namespace:ZXing.Net.Maui.Controls;assembly=ZXing.Net.Maui.Controls"
             xmlns:vm="clr-namespace:OpenAgent.App.ViewModels"
             x:Class="OpenAgent.App.Pages.OnboardingPage"
             x:DataType="vm:OnboardingViewModel"
             Title="Connect to agent"
             BackgroundColor="Black">
  <Grid>
    <zxing:CameraBarcodeReaderView x:Name="Reader" BarcodesDetected="OnBarcodesDetected" />
    <VerticalStackLayout VerticalOptions="End" Padding="24" Spacing="16">
      <Label Text="Point camera at the QR code from the agent" TextColor="White" HorizontalOptions="Center" />
      <Label Text="{Binding Error}" TextColor="#FF6B6B" IsVisible="{Binding HasError}" HorizontalOptions="Center" />
      <Button Text="Enter manually" Command="{Binding OpenManualEntryCommand}" />
    </VerticalStackLayout>
  </Grid>
</ContentPage>
```

**Step 4: OnboardingPage code-behind.**

```csharp
using ZXing.Net.Maui;
using OpenAgent.App.ViewModels;

namespace OpenAgent.App.Pages;

public partial class OnboardingPage : ContentPage
{
    private readonly OnboardingViewModel _vm;
    private bool _handled;

    public OnboardingPage(OnboardingViewModel vm)
    {
        InitializeComponent();
        BindingContext = _vm = vm;
    }

    private async void OnBarcodesDetected(object? sender, BarcodeDetectionEventArgs e)
    {
        if (_handled) return;
        var text = e.Results?.FirstOrDefault()?.Value;
        if (string.IsNullOrEmpty(text)) return;
        _handled = true;
        await MainThread.InvokeOnMainThreadAsync(() => _vm.OnQrScannedCommand.ExecuteAsync(text));
    }
}
```

> `BarcodesDetected` event name and `BarcodeDetectionEventArgs` shape verified against ZXing.Net.Maui 0.4.0. If installing a different version, adjust to whatever the package exposes. The handler runs on a background thread on iOS — marshal to main thread before touching VM state. The `_handled` latch prevents re-entry from rapid follow-up scans of the same QR.

**Step 5: ManualEntryPage.xaml.**

```xml
<?xml version="1.0" encoding="utf-8" ?>
<ContentPage xmlns="http://schemas.microsoft.com/dotnet/2021/maui"
             xmlns:x="http://schemas.microsoft.com/winfx/2009/xaml"
             xmlns:vm="clr-namespace:OpenAgent.App.ViewModels"
             x:Class="OpenAgent.App.Pages.ManualEntryPage"
             x:DataType="vm:ManualEntryViewModel"
             Title="Enter manually">
  <VerticalStackLayout Padding="24" Spacing="16">
    <Label Text="Server URL" />
    <Entry Text="{Binding ServerUrl}" Placeholder="https://agent.example.com" Keyboard="Url" />
    <Label Text="API token" />
    <Entry Text="{Binding Token}" IsPassword="True" />
    <Label Text="{Binding Error}" TextColor="#FF6B6B" IsVisible="{Binding HasError}" />
    <Button Text="Save" Command="{Binding SaveCommand}" />
  </VerticalStackLayout>
</ContentPage>
```

**Step 6: ManualEntryPage code-behind.**

```csharp
using OpenAgent.App.ViewModels;

namespace OpenAgent.App.Pages;

public partial class ManualEntryPage : ContentPage
{
    public ManualEntryPage(ManualEntryViewModel vm)
    {
        InitializeComponent();
        BindingContext = vm;
    }
}
```

**Step 7: Commit.**

```bash
git commit -am "feat(app): OnboardingPage (QR) + ManualEntryPage"
```

---

### Task 16: Conversations page

**Files:**
- Create: `src/app/OpenAgent.App/Pages/ConversationsPage.xaml` + `.xaml.cs`
- Create: `src/app/OpenAgent.App/ViewModels/ConversationsViewModel.cs`
- Create: `src/app/OpenAgent.App/Converters/RelativeTimeConverter.cs`

**Step 1: ViewModel** with cache-first refresh, swipe-delete, rename, FAB:

```csharp
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenAgent.App.Core.Api;
using OpenAgent.App.Core.Models;
using OpenAgent.App.Core.Services;

namespace OpenAgent.App.ViewModels;

public partial class ConversationsViewModel : ObservableObject
{
    private readonly IApiClient _api;
    private readonly ConversationCache _cache;

    public ObservableCollection<ConversationListItem> Items { get; } = new();
    [ObservableProperty] private bool _isOffline;
    [ObservableProperty] private bool _isRefreshing;

    public ConversationsViewModel(IApiClient api, ConversationCache cache)
    {
        _api = api;
        _cache = cache;
    }

    [RelayCommand]
    public async Task LoadAsync()
    {
        IsRefreshing = true;

        var cached = await _cache.ReadAsync();
        if (cached is not null)
        {
            Replace(cached);
        }

        try
        {
            var fresh = await _api.GetConversationsAsync();
            await _cache.WriteAsync(fresh);
            Replace(fresh);
            IsOffline = false;
        }
        catch (AuthRejectedException)
        {
            await Shell.Current.DisplayAlert("Authentication failed",
                "The agent rejected the API token. Please reconfigure.", "Reconfigure");
            await Shell.Current.GoToAsync("//onboarding");
        }
        catch
        {
            IsOffline = cached is not null;
            if (cached is null)
                await Shell.Current.DisplayAlert("Offline", "Couldn't reach agent.", "OK");
        }
        finally
        {
            IsRefreshing = false;
        }
    }

    [RelayCommand]
    public async Task DeleteAsync(ConversationListItem item)
    {
        var ok = await Shell.Current.DisplayAlert("Delete?", $"Delete \"{item.Title}\"?", "Delete", "Cancel");
        if (!ok) return;
        try { await _api.DeleteConversationAsync(item.Id); Items.Remove(item); }
        catch { await Shell.Current.DisplayAlert("Failed", "Could not delete.", "OK"); }
    }

    [RelayCommand]
    public async Task RenameAsync(ConversationListItem item)
    {
        var name = await Shell.Current.DisplayPromptAsync("Rename", "New title", initialValue: item.Intention ?? item.DisplayName ?? "");
        if (string.IsNullOrWhiteSpace(name)) return;
        try { await _api.RenameConversationAsync(item.Id, name); await LoadAsync(); }
        catch { await Shell.Current.DisplayAlert("Failed", "Could not rename.", "OK"); }
    }

    [RelayCommand]
    public Task NewCallAsync()
    {
        var id = Guid.NewGuid().ToString();
        return Shell.Current.GoToAsync($"call?conversationId={id}&title=New+conversation");
    }

    [RelayCommand]
    public Task OpenAsync(ConversationListItem item)
        => Shell.Current.GoToAsync($"call?conversationId={item.Id}&title={Uri.EscapeDataString(item.Title)}");

    [RelayCommand]
    public Task OpenSettingsAsync() => Shell.Current.GoToAsync("settings");

    private void Replace(IEnumerable<ConversationListItem> fresh)
    {
        Items.Clear();
        foreach (var i in fresh.OrderByDescending(x => x.SortKey))
            Items.Add(i);
    }
}
```

**Step 2: XAML** with `RefreshView`, `CollectionView`, `SwipeView`, FAB, source badge.

```xml
<?xml version="1.0" encoding="utf-8" ?>
<ContentPage xmlns="http://schemas.microsoft.com/dotnet/2021/maui"
             xmlns:x="http://schemas.microsoft.com/winfx/2009/xaml"
             xmlns:vm="clr-namespace:OpenAgent.App.ViewModels"
             xmlns:m="clr-namespace:OpenAgent.App.Core.Models;assembly=OpenAgent.App.Core"
             xmlns:c="clr-namespace:OpenAgent.App.Converters"
             x:Class="OpenAgent.App.Pages.ConversationsPage"
             x:DataType="vm:ConversationsViewModel"
             Title="Conversations">
  <ContentPage.Resources>
    <c:RelativeTimeConverter x:Key="RelativeTime" />
  </ContentPage.Resources>
  <ContentPage.ToolbarItems>
    <ToolbarItem Text="Settings" Command="{Binding OpenSettingsCommand}" />
  </ContentPage.ToolbarItems>

  <Grid>
    <RefreshView IsRefreshing="{Binding IsRefreshing}" Command="{Binding LoadCommand}">
      <CollectionView ItemsSource="{Binding Items}" SelectionMode="None">
        <CollectionView.EmptyView>
          <VerticalStackLayout HorizontalOptions="Center" VerticalOptions="Center" Spacing="8">
            <Label Text="No conversations yet" FontAttributes="Bold" />
            <Label Text="Tap + to start one" />
          </VerticalStackLayout>
        </CollectionView.EmptyView>
        <CollectionView.ItemTemplate>
          <DataTemplate x:DataType="m:ConversationListItem">
            <SwipeView>
              <SwipeView.LeftItems>
                <SwipeItems>
                  <SwipeItem Text="Rename" BackgroundColor="#0A84FF"
                             Command="{Binding Source={RelativeSource AncestorType={x:Type vm:ConversationsViewModel}}, Path=RenameCommand}"
                             CommandParameter="{Binding .}" />
                </SwipeItems>
              </SwipeView.LeftItems>
              <SwipeView.RightItems>
                <SwipeItems>
                  <SwipeItem Text="Delete" BackgroundColor="Red"
                             Command="{Binding Source={RelativeSource AncestorType={x:Type vm:ConversationsViewModel}}, Path=DeleteCommand}"
                             CommandParameter="{Binding .}" />
                </SwipeItems>
              </SwipeView.RightItems>
              <Grid Padding="16,12" ColumnDefinitions="*,Auto" RowDefinitions="Auto,Auto">
                <Grid.GestureRecognizers>
                  <TapGestureRecognizer Command="{Binding Source={RelativeSource AncestorType={x:Type vm:ConversationsViewModel}}, Path=OpenCommand}"
                                        CommandParameter="{Binding .}" />
                </Grid.GestureRecognizers>
                <Label Grid.Column="0" Grid.Row="0" Text="{Binding Title}" FontSize="16" />
                <Label Grid.Column="0" Grid.Row="1" Text="{Binding Source}" FontSize="11" Opacity="0.6" />
                <Label Grid.Column="1" Grid.Row="0" Text="{Binding SortKey, Converter={StaticResource RelativeTime}}" FontSize="12" Opacity="0.6" />
              </Grid>
            </SwipeView>
          </DataTemplate>
        </CollectionView.ItemTemplate>
      </CollectionView>
    </RefreshView>

    <Button Text="+" Command="{Binding NewCallCommand}"
            BackgroundColor="#0A84FF" TextColor="White"
            CornerRadius="28" WidthRequest="56" HeightRequest="56"
            HorizontalOptions="End" VerticalOptions="End" Margin="0,0,24,24" />
  </Grid>
</ContentPage>
```

**Step 3: Code-behind** invokes Load on appear.

```csharp
using OpenAgent.App.ViewModels;

namespace OpenAgent.App.Pages;

public partial class ConversationsPage : ContentPage
{
    private readonly ConversationsViewModel _vm;
    public ConversationsPage(ConversationsViewModel vm)
    {
        InitializeComponent();
        BindingContext = _vm = vm;
    }
    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await _vm.LoadCommand.ExecuteAsync(null);
    }
}
```

**Step 4: Converters.** Both `RelativeTimeConverter` and `MuteLabelConverter` go in one file. Register both as resources in `App.xaml`.

`Converters/RelativeTimeConverter.cs`:

```csharp
using System.Globalization;
namespace OpenAgent.App.Converters;

public sealed class RelativeTimeConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not DateTimeOffset dt) return "—";
        var d = DateTimeOffset.UtcNow - dt;
        if (d.TotalMinutes < 1) return "just now";
        if (d.TotalMinutes < 60) return $"{(int)d.TotalMinutes}m ago";
        if (d.TotalHours < 24) return $"{(int)d.TotalHours}h ago";
        if (d.TotalDays < 7) return $"{(int)d.TotalDays}d ago";
        return dt.LocalDateTime.ToString("d MMM");
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class MuteLabelConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => (value is bool muted && muted) ? "Unmute" : "Mute";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
```

Add to `App.xaml` resource dictionary alongside the merged dictionaries:

```xml
<ResourceDictionary>
  <ResourceDictionary.MergedDictionaries>
    <ResourceDictionary Source="Resources/Styles/Colors.xaml" />
    <ResourceDictionary Source="Resources/Styles/Styles.xaml" />
  </ResourceDictionary.MergedDictionaries>
  <converters:RelativeTimeConverter x:Key="RelativeTime" xmlns:converters="clr-namespace:OpenAgent.App.Converters" />
  <converters:MuteLabelConverter    x:Key="MuteLabel"    xmlns:converters="clr-namespace:OpenAgent.App.Converters" />
</ResourceDictionary>
```

**Step 5: Commit.**

```bash
git commit -am "feat(app): ConversationsPage with cache + swipe-delete + rename"
```

---

### Task 17: Call page (audio + transcript pane)

**Files:**
- Create: `src/app/OpenAgent.App/Pages/CallPage.xaml` + `.xaml.cs`
- Create: `src/app/OpenAgent.App/ViewModels/CallViewModel.cs`

**Step 1: ViewModel.** Single owning task drives connect → run → maybe-reconnect → end. UI mutations and Shell calls always marshal to the main thread.

The `IVoiceWebSocketClient` is registered as transient, so each call to `StartSessionAsync` resolves a fresh instance via the service provider — old instances are disposed in `TeardownAsync` before a reconnect.

```csharp
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using OpenAgent.App.Core.Voice;

namespace OpenAgent.App.ViewModels;

[QueryProperty(nameof(ConversationId), "conversationId")]
[QueryProperty(nameof(Title), "title")]
public partial class CallViewModel : ObservableObject, IDisposable
{
    private readonly IServiceProvider _services;
    private readonly ICallAudio _audio;
    private readonly CallStateMachine _sm = new();
    private readonly ReconnectBackoff _backoff = new();

    private IVoiceWebSocketClient? _ws;
    private CancellationTokenSource? _cts;
    private TranscriptRouter? _transcript;
    private Task? _runner;
    private bool _ended;

    [ObservableProperty] private string? _conversationId;
    [ObservableProperty] private string? _title;
    [ObservableProperty] private CallState _state;
    [ObservableProperty] private bool _muted;
    [ObservableProperty] private bool _showTranscript;

    public ObservableCollection<TranscriptBubble> Bubbles { get; } = new();

    public CallViewModel(IServiceProvider services, ICallAudio audio)
    {
        _services = services;
        _audio = audio;
        _audio.OnPcmCaptured += pcm =>
        {
            // Fire-and-forget: SendAudioAsync serializes internally via SemaphoreSlim.
            var ws = _ws;
            var token = _cts?.Token ?? CancellationToken.None;
            if (ws is null || token.IsCancellationRequested) return;
            _ = ws.SendAudioAsync(pcm, token);
        };
    }

    [RelayCommand]
    public Task StartAsync()
    {
        _cts = new CancellationTokenSource();
        _ended = false;
        _transcript = new TranscriptRouter(
            onAppend: (src, t) => MainThread.BeginInvokeOnMainThread(() => Bubbles.Add(new TranscriptBubble(src, t))),
            onUpdateLast: (t) => MainThread.BeginInvokeOnMainThread(() =>
            {
                if (Bubbles.Count > 0) Bubbles[^1] = Bubbles[^1] with { Text = t };
            }));

        _runner = Task.Run(() => RunSessionLoopAsync(_cts.Token));
        return Task.CompletedTask;
    }

    /// <summary>Owns the entire session: each iteration is one connect → drain → optional reconnect.</summary>
    private async Task RunSessionLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && !_ended)
        {
            SetState(CallState.Connecting, viaMachine: sm => sm.OnConnecting());

            try
            {
                _ws = _services.GetRequiredService<IVoiceWebSocketClient>();
                await _ws.ConnectAsync(ConversationId!, ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                await TeardownAsync();
                if (!await TryReconnectOrFinishAsync(ex.Message, authRejected: false, ct)) return;
                continue;
            }

            _backoff.Reset();
            var disconnect = await DrainFramesAsync(ct);
            await TeardownAsync();

            if (ct.IsCancellationRequested || _ended) return;

            if (disconnect.AuthRejected)
            {
                await OnMain(() => Shell.Current.DisplayAlert("Authentication failed",
                    "Token rejected by agent. Please reconfigure.", "OK"));
                await OnMain(() => Shell.Current.GoToAsync("//onboarding"));
                return;
            }

            if (!await TryReconnectOrFinishAsync(disconnect.Reason ?? "Connection lost", authRejected: false, ct)) return;
        }
    }

    /// <summary>Reads frames until the WebSocket disconnects. Returns the disconnect frame.</summary>
    private async Task<VoiceFrame.Disconnected> DrainFramesAsync(CancellationToken ct)
    {
        var ws = _ws!;
        await foreach (var frame in ws.ReadFramesAsync(ct))
        {
            switch (frame)
            {
                case VoiceFrame.EventFrame { Event: VoiceEvent.SessionReady ready }:
                    if (!string.Equals(ready.InputCodec, "pcm16", StringComparison.OrdinalIgnoreCase)
                        || !string.Equals(ready.OutputCodec, "pcm16", StringComparison.OrdinalIgnoreCase))
                    {
                        await OnMain(() => Shell.Current.DisplayAlert("Codec mismatch",
                            $"Agent announced {ready.InputCodec}/{ready.OutputCodec}; this client only handles pcm16.", "OK"));
                        return new VoiceFrame.Disconnected("Unsupported codec", AuthRejected: false);
                    }
                    await _audio.StartAsync(ready.InputSampleRate, ct);
                    SetState(CallState.Listening, viaMachine: sm => sm.Apply(ready));
                    break;

                case VoiceFrame.EventFrame ef:
                    if (ef.Event is VoiceEvent.SpeechStarted) _audio.FlushPlayback();
                    if (ef.Event is VoiceEvent.TranscriptDelta td) _transcript!.OnDelta(td.Source, td.Text);
                    if (ef.Event is VoiceEvent.TranscriptDone) _transcript!.OnDone();
                    SetState(_sm.State, viaMachine: sm => sm.Apply(ef.Event));
                    if (ef.Event is VoiceEvent.Error err)
                        await OnMain(() => Shell.Current.DisplayAlert("Voice error", err.Message, "OK"));
                    break;

                case VoiceFrame.AudioFrame af:
                    _audio.EnqueuePlayback(af.Pcm16);
                    SetState(CallState.AssistantSpeaking, viaMachine: sm => sm.OnAudioReceived());
                    break;

                case VoiceFrame.Disconnected d:
                    return d;
            }
        }
        return new VoiceFrame.Disconnected("Stream ended", AuthRejected: false);
    }

    private async Task<bool> TryReconnectOrFinishAsync(string reason, bool authRejected, CancellationToken ct)
    {
        if (authRejected) return false;
        if (_backoff.GiveUp || ct.IsCancellationRequested)
        {
            await OnMain(() => Shell.Current.DisplayAlert("Disconnected", reason, "OK"));
            await OnMain(() => Shell.Current.GoToAsync(".."));
            return false;
        }
        SetState(CallState.Reconnecting, viaMachine: sm => sm.OnReconnecting());
        try { await Task.Delay(_backoff.NextDelay(), ct); }
        catch (OperationCanceledException) { return false; }
        return true;
    }

    private async Task TeardownAsync()
    {
        var ws = _ws;
        _ws = null;
        if (ws is not null)
        {
            try { await ws.DisposeAsync(); } catch { }
        }
        try { await _audio.StopAsync(); } catch { }
    }

    [RelayCommand]
    public void ToggleMute()
    {
        Muted = !Muted;
        _audio.SetMuted(Muted);
    }

    [RelayCommand]
    public async Task EndAsync()
    {
        _ended = true;
        _cts?.Cancel();
        await TeardownAsync();
        if (_runner is not null) { try { await _runner; } catch { } }
        await Shell.Current.GoToAsync("..");
    }

    [RelayCommand]
    public void ToggleTranscript() => ShowTranscript = !ShowTranscript;

    private void SetState(CallState newState, Action<CallStateMachine> viaMachine)
    {
        viaMachine(_sm);
        var s = _sm.State;
        MainThread.BeginInvokeOnMainThread(() => State = s);
    }

    private static Task OnMain(Func<Task> action)
    {
        var tcs = new TaskCompletionSource();
        MainThread.BeginInvokeOnMainThread(async () =>
        {
            try { await action(); tcs.TrySetResult(); }
            catch (Exception ex) { tcs.TrySetException(ex); }
        });
        return tcs.Task;
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
    }
}

public sealed record TranscriptBubble(TranscriptSource Source, string Text);
```

**Step 2: XAML.**

```xml
<?xml version="1.0" encoding="utf-8" ?>
<ContentPage xmlns="http://schemas.microsoft.com/dotnet/2021/maui"
             xmlns:x="http://schemas.microsoft.com/winfx/2009/xaml"
             xmlns:vm="clr-namespace:OpenAgent.App.ViewModels"
             xmlns:v="clr-namespace:OpenAgent.App.Core.Voice;assembly=OpenAgent.App.Core"
             x:Class="OpenAgent.App.Pages.CallPage"
             x:DataType="vm:CallViewModel"
             Shell.PresentationMode="ModalAnimated"
             BackgroundColor="Black">
  <Grid RowDefinitions="Auto,*,Auto,Auto" Padding="32" ColumnDefinitions="*">
    <!-- Title -->
    <Label Grid.Row="0" Text="{Binding Title}" TextColor="White" FontSize="24" HorizontalOptions="Center" Margin="0,40,0,0" />
    <!-- Avatar + state -->
    <VerticalStackLayout Grid.Row="1" VerticalOptions="Center" HorizontalOptions="Center" Spacing="24">
      <Border WidthRequest="160" HeightRequest="160" Background="#0A84FF" StrokeThickness="0">
        <Border.StrokeShape>
          <RoundRectangle CornerRadius="80" />
        </Border.StrokeShape>
        <Label Text="A" TextColor="White" FontSize="64" HorizontalOptions="Center" VerticalOptions="Center" />
      </Border>
      <Label Text="{Binding State}" TextColor="#CCC" HorizontalOptions="Center" />
      <Button Text="Transcript" Command="{Binding ToggleTranscriptCommand}" BackgroundColor="Transparent" TextColor="#888" />
      <CollectionView IsVisible="{Binding ShowTranscript}" ItemsSource="{Binding Bubbles}" HeightRequest="200">
        <CollectionView.ItemTemplate>
          <DataTemplate x:DataType="vm:TranscriptBubble">
            <Label Text="{Binding Text}" TextColor="White" Margin="8,4" />
          </DataTemplate>
        </CollectionView.ItemTemplate>
      </CollectionView>
    </VerticalStackLayout>

    <!-- Controls. Mute label flips between "Mute" and "Unmute" via a converter on the bool;
         no per-state color change to keep the styling single-source (one color per button). -->
    <HorizontalStackLayout Grid.Row="3" HorizontalOptions="Center" Spacing="48" Margin="0,0,0,40">
      <Button Text="{Binding Muted, Converter={StaticResource MuteLabel}}"
              WidthRequest="72" HeightRequest="72" CornerRadius="36"
              BackgroundColor="#444" TextColor="White"
              Command="{Binding ToggleMuteCommand}" />
      <Button Text="End" WidthRequest="72" HeightRequest="72" CornerRadius="36"
              BackgroundColor="#FF3B30" TextColor="White"
              Command="{Binding EndCommand}" />
    </HorizontalStackLayout>
  </Grid>
</ContentPage>
```

**Step 3: Code-behind.**

```csharp
using OpenAgent.App.ViewModels;

namespace OpenAgent.App.Pages;

public partial class CallPage : ContentPage
{
    private readonly CallViewModel _vm;
    public CallPage(CallViewModel vm)
    {
        InitializeComponent();
        BindingContext = _vm = vm;
    }
    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await _vm.StartCommand.ExecuteAsync(null);
    }
    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        _vm.Dispose();
    }
}
```

**Step 4: Commit.**

```bash
git commit -am "feat(app): CallPage with state machine + reconnect + transcript pane"
```

---

### Task 18: Settings page

**Files:**
- Create: `src/app/OpenAgent.App/Pages/SettingsPage.xaml` + `.xaml.cs`
- Create: `src/app/OpenAgent.App/ViewModels/SettingsViewModel.cs`

**Step 1: VM.**

```csharp
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenAgent.App.Core.Services;

namespace OpenAgent.App.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    private readonly ICredentialStore _store;
    [ObservableProperty] private string _serverUrl = "";
    [ObservableProperty] private string _token = "";
    [ObservableProperty] private bool _showToken;
    [ObservableProperty] private string _appVersion = AppInfo.Current.VersionString;

    public SettingsViewModel(ICredentialStore store) { _store = store; }

    [RelayCommand]
    public async Task LoadAsync()
    {
        var c = await _store.LoadAsync();
        ServerUrl = c?.BaseUrl ?? "";
        Token = c?.Token ?? "";
    }

    [RelayCommand]
    public async Task ReconfigureAsync()
    {
        await _store.ClearAsync();
        await Shell.Current.GoToAsync("//onboarding");
    }

    [RelayCommand]
    public void ToggleReveal() => ShowToken = !ShowToken;
}
```

**Step 2: XAML.** Two read-only labels, one masked label with a reveal toggle, two buttons. Skip code listing — straightforward.

**Step 3: Commit.**

```bash
git commit -am "feat(app): SettingsPage"
```

---

## Phase 5 — CI / deploy

### Task 19: GitHub Actions iOS build workflow

**Files:**
- Create: `.github/workflows/ios-build.yml`

**Step 1: Workflow** — build-only on PR, build + TestFlight on `app-v*` tag.

```yaml
name: iOS app build

on:
  push:
    branches: [master]
    tags: ['app-v*']
    paths: ['src/app/**', '.github/workflows/ios-build.yml']
  pull_request:
    paths: ['src/app/**', '.github/workflows/ios-build.yml']
  workflow_dispatch:

jobs:
  build:
    runs-on: macos-14
    steps:
      - uses: actions/checkout@v4

      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: '10.0.x'

      - name: Install MAUI workload
        run: dotnet workload install maui-ios

      - name: Run Core tests
        working-directory: src/app
        run: dotnet test OpenAgent.App.Tests/OpenAgent.App.Tests.csproj --logger "trx" --logger "console;verbosity=detailed"

      - name: Build (Debug)
        if: ${{ !startsWith(github.ref, 'refs/tags/app-v') }}
        working-directory: src/app
        run: dotnet build OpenAgent.App/OpenAgent.App.csproj -c Debug -f net10.0-ios

      - name: Import code-signing certs
        if: startsWith(github.ref, 'refs/tags/app-v')
        uses: apple-actions/import-codesign-certs@v2
        with:
          p12-file-base64: ${{ secrets.IOS_DIST_CERT_P12 }}
          p12-password: ${{ secrets.IOS_DIST_CERT_PASSWORD }}

      - name: Install provisioning profile
        if: startsWith(github.ref, 'refs/tags/app-v')
        env:
          PROFILE: ${{ secrets.IOS_PROVISIONING_PROFILE }}
        run: |
          mkdir -p ~/Library/MobileDevice/Provisioning\ Profiles
          echo "$PROFILE" | base64 -d > ~/Library/MobileDevice/Provisioning\ Profiles/profile.mobileprovision

      - name: Build & archive (Release, signed)
        if: startsWith(github.ref, 'refs/tags/app-v')
        working-directory: src/app
        run: |
          dotnet publish OpenAgent.App/OpenAgent.App.csproj \
            -c Release -f net10.0-ios \
            -p:ArchiveOnBuild=true \
            -p:CodesignKey="Apple Distribution" \
            -p:CodesignProvision="OpenAgent App Store"

      - name: Locate IPA
        id: ipa
        if: startsWith(github.ref, 'refs/tags/app-v')
        run: |
          IPA=$(find src/app/OpenAgent.App/bin/Release -name 'OpenAgent.App.ipa' | head -n1)
          if [ -z "$IPA" ]; then echo "No .ipa found under bin/Release"; ls -R src/app/OpenAgent.App/bin/Release; exit 1; fi
          echo "path=$IPA" >> "$GITHUB_OUTPUT"

      - name: Upload to TestFlight
        if: startsWith(github.ref, 'refs/tags/app-v')
        uses: apple-actions/upload-testflight-build@v1
        with:
          app-path: ${{ steps.ipa.outputs.path }}
          issuer-id: ${{ secrets.APPSTORE_API_ISSUER_ID }}
          api-key-id: ${{ secrets.APPSTORE_API_KEY_ID }}
          api-private-key: ${{ secrets.APPSTORE_API_KEY_P8 }}

      - name: Upload IPA artifact
        if: startsWith(github.ref, 'refs/tags/app-v')
        uses: actions/upload-artifact@v4
        with:
          name: OpenAgent-App-ipa
          path: ${{ steps.ipa.outputs.path }}
```

**Step 2: Commit.**

```bash
git add .github/workflows/ios-build.yml
git commit -m "ci: iOS app build + TestFlight on tag"
```

> Reality check: provisioning-profile setup names are placeholders. The first CI run will fail; you'll iterate on signing parameters until it sticks. That's normal MAUI iOS CI work — debugging is in the workflow logs, not in the app code.

---

### Task 20: README for `src/app/`

**Files:**
- Create: `src/app/README.md`

Document:
- Architecture summary (link to design doc).
- How to build the Core lib + run tests on Windows.
- How to build the iOS head (requires Mac with Xcode + .NET 10 SDK + MAUI workload).
- TestFlight tag flow: `git tag app-v0.1.0 && git push --tags`.
- Required GitHub secrets and how to obtain them.
- Known limits: from-Windows untested code paths (audio, Keychain, camera, real WS).

Commit:

```bash
git commit -am "docs(app): README for src/app/"
```

---

## Done condition

The plan is finished when:

1. `cd src/app && dotnet test` passes on Windows.
2. The CI iOS-build job succeeds on a green-path commit (build only, no signing).
3. A TestFlight build from tag `app-v0.1.0` reaches the Apple sandbox and the user has installed it on their iPhone.
4. The user has done one round trip on the device: scan QR → see conversation list → tap `+` → talk to the agent → end call. (This is the manual smoke test — there's no way around needing to do it once on a real device.)

Items 1–2 are verifiable from this session. Items 3–4 are user-driven and are explicitly out-of-scope of "Claude finishes the implementation".

## Open follow-ups (not in this plan)

- Agent-side QR rendering on startup so the user doesn't have to generate a QR externally from the printed URL.
- Exposing `personality.name` via `/api/agent` so the call screen can display the right name.
- Outbound calls (agent rings the user). Needs APNs + likely CallKit.
- Per-conversation provider/voice picker.
- App icon / branding (placeholder ships in v0.1.0).
