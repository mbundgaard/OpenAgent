# Telnyx Real-Time Voice Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a Telnyx Media Streaming bridge that brings inbound phone calls into an `ILlmVoiceProvider` real-time session, with the same tool surface as text channels, barge-in, agent-initiated hangup, and a "thinking" audio cue while tools run.

**Architecture:** Telnyx Call Control opens a bidirectional WebSocket carrying JSON-envelope `media` events with base64-encoded µ-law 8 kHz audio. A per-call `TelnyxMediaBridge` pumps bytes between that WebSocket and an `ILlmVoiceProvider` session. The voice provider gains a per-session `VoiceSessionOptions(Codec, SampleRate)` so the Telnyx bridge requests `g711_ulaw 8000` while the existing browser path keeps `pcm16 24000` as the default. Conversations are E.164-keyed (`ConversationType.Phone`), the agent can hang up via an `EndCallTool`, and a procedurally-generated thinking clip plays during tool execution.

**Tech Stack:** .NET 10, ASP.NET Core minimal APIs + WebSockets, System.Text.Json, BouncyCastle (ED25519 signature verification), xUnit + `WebApplicationFactory<Program>` for integration tests. No new third-party services or NuGet packages beyond what's already in `Directory.Packages.props` (BouncyCastle was added on the prior P2 branch and is included in master via the merge).

Spec reference: `docs/superpowers/specs/2026-04-25-telnyx-realtime-voice-design.md`.

Suggested PR split (the tasks below are sequential — split where convenient):

- **PR1 — Voice contract changes (Tasks 1–7).** Adds `VoiceSessionOptions`, refactors voice providers, adds `ConversationType.Phone` and `PHONE.md`, adds browser thinking-cue events. Ships independently — browser voice path unchanged behaviourally, just refactored.
- **PR2 — Telnyx primitives (Tasks 8–14).** New `OpenAgent.Channel.Telnyx` project with options, signature verifier, media-frame parsing, REST client, thinking-clip factory. Compiles, unit-tested, but unwired — no behaviour change.
- **PR3 — Telnyx integration (Tasks 15–25).** Provider, factory, webhook endpoints, streaming endpoint, media bridge, `EndCallTool`, DI wiring, cancellation audit. End-to-end working.

---

## File Structure

| File | Status | Owner Task |
|---|---|---|
| `src/agent/OpenAgent.Models/Voice/VoiceSessionOptions.cs` | new | 1 |
| `src/agent/OpenAgent.Contracts/ILlmVoiceProvider.cs` | modified | 1 |
| `src/agent/OpenAgent.LlmVoice.OpenAIAzure/AzureOpenAiRealtimeVoiceProvider.cs` | modified | 2 |
| `src/agent/OpenAgent.LlmVoice.OpenAIAzure/AzureOpenAiVoiceSession.cs` | modified | 2 |
| `src/agent/OpenAgent.LlmVoice.GrokRealtime/GrokRealtimeVoiceProvider.cs` | modified | 3 |
| `src/agent/OpenAgent.LlmVoice.GrokRealtime/GrokVoiceSession.cs` | modified | 3 |
| `src/agent/OpenAgent.LlmVoice.GeminiLive/GeminiLiveVoiceProvider.cs` | modified | 4 |
| `src/agent/OpenAgent.Models/Conversations/Conversation.cs` | modified | 5 |
| `src/agent/OpenAgent/defaults/PHONE.md` | new | 5 |
| `src/agent/OpenAgent/SystemPromptBuilder.cs` | modified | 5 |
| `src/agent/OpenAgent.Models/Voice/VoiceWebSocketEvents.cs` | modified | 6 |
| `src/agent/OpenAgent.Api/Endpoints/WebSocketVoiceEndpoints.cs` | modified | 6 |
| `src/agent/OpenAgent.Api/Endpoints/WebSocketVoiceEndpoints.cs` | modified (browser endpoint passes null options) | 7 |
| `src/agent/OpenAgent.Channel.Telnyx/OpenAgent.Channel.Telnyx.csproj` | new | 8 |
| `src/agent/OpenAgent.Channel.Telnyx/TelnyxOptions.cs` | new | 9 |
| `src/agent/OpenAgent.Channel.Telnyx/TelnyxSignatureVerifier.cs` | new | 10 |
| `src/agent/OpenAgent.Channel.Telnyx/TelnyxMediaFrame.cs` | new | 11 |
| `src/agent/OpenAgent.Channel.Telnyx/TelnyxCallControlClient.cs` | new | 12 |
| `src/agent/OpenAgent.Channel.Telnyx/ThinkingClipFactory.cs` | new | 13 |
| `src/agent/OpenAgent.Channel.Telnyx/TelnyxBridgeRegistry.cs` | new | 14 |
| `src/agent/OpenAgent.Channel.Telnyx/TelnyxChannelProviderFactory.cs` | new | 15 |
| `src/agent/OpenAgent.Channel.Telnyx/TelnyxChannelProvider.cs` | new | 15 |
| `src/agent/OpenAgent.Channel.Telnyx/TelnyxWebhookEndpoints.cs` | new | 16 |
| `src/agent/OpenAgent.Channel.Telnyx/TelnyxStreamingEndpoint.cs` | new | 17 |
| `src/agent/OpenAgent.Channel.Telnyx/TelnyxMediaBridge.cs` | new | 18–22 |
| `src/agent/OpenAgent.Channel.Telnyx/EndCallTool.cs` | new | 23 |
| `src/agent/OpenAgent/Program.cs` | modified | 24 |
| `src/agent/OpenAgent.Tools.WebFetch/WebFetchTool.cs` (and ShellExecTool) | audit-only | 25 |

---

## Task 1: VoiceSessionOptions record + ILlmVoiceProvider parameter

**Files:**

- Create: `src/agent/OpenAgent.Models/Voice/VoiceSessionOptions.cs`
- Modify: `src/agent/OpenAgent.Contracts/ILlmVoiceProvider.cs`

- [ ] **Step 1: Write the new record.**

```csharp
// src/agent/OpenAgent.Models/Voice/VoiceSessionOptions.cs
namespace OpenAgent.Models.Voice;

/// <summary>
/// Per-session audio configuration. Channels override the provider's default codec and sample rate.
/// </summary>
/// <param name="Codec">Codec identifier as understood by the provider (pcm16, g711_ulaw, g711_alaw).</param>
/// <param name="SampleRate">Sample rate in Hz (8000 for g711_*, 24000 for pcm16 on Azure/Grok).</param>
public sealed record VoiceSessionOptions(string Codec, int SampleRate);
```

- [ ] **Step 2: Modify `ILlmVoiceProvider.StartSessionAsync` signature.**

Open `src/agent/OpenAgent.Contracts/ILlmVoiceProvider.cs` and replace the method signature:

```csharp
using OpenAgent.Models.Conversations;
using OpenAgent.Models.Voice;

namespace OpenAgent.Contracts;

public interface ILlmVoiceProvider: IConfigurable
{
    /// <summary>Opens a new bidirectional voice session with the configured backend.</summary>
    /// <param name="conversation">Owning conversation; the session inherits its system prompt and history.</param>
    /// <param name="options">Per-session codec/rate. When null the provider uses its default (pcm16 24 kHz on Azure/Grok).</param>
    Task<IVoiceSession> StartSessionAsync(
        Conversation conversation,
        VoiceSessionOptions? options = null,
        CancellationToken ct = default);
}
```

- [ ] **Step 3: Build to confirm the interface change compiles before any provider is updated.**

Run from `src/agent/`:

```bash
dotnet build OpenAgent.Contracts/OpenAgent.Contracts.csproj
```

Expected: `Build succeeded` for that project (the providers will fail in step 5 — that's expected).

- [ ] **Step 4: Build the whole solution and confirm the expected compile errors.**

```bash
dotnet build
```

Expected: failures in `AzureOpenAiRealtimeVoiceProvider`, `GrokRealtimeVoiceProvider`, `GeminiLiveVoiceProvider`, `VoiceSessionManager` — they implement/call the old signature. Tasks 2–4 fix them.

- [ ] **Step 5: Commit.**

```bash
git add src/agent/OpenAgent.Models/Voice/VoiceSessionOptions.cs src/agent/OpenAgent.Contracts/ILlmVoiceProvider.cs
git commit -m "feat(voice): add VoiceSessionOptions parameter to ILlmVoiceProvider"
```

The build is intentionally broken between this and Task 2; commit anyway so the contract change is its own atomic unit.

---

## Task 2: Update Azure Realtime voice provider for VoiceSessionOptions

**Files:**

- Modify: `src/agent/OpenAgent.LlmVoice.OpenAIAzure/AzureOpenAiRealtimeVoiceProvider.cs`
- Modify: `src/agent/OpenAgent.LlmVoice.OpenAIAzure/AzureOpenAiVoiceSession.cs`
- Modify: any tests that asserted the `codec` config field

- [ ] **Step 1: Find the existing codec test, if any, and write a new failing test for per-session override.**

```bash
grep -rn 'codec\|Codec' OpenAgent.Tests/ | grep -i azure
```

Add `OpenAgent.Tests/AzureOpenAiVoiceProviderOptionsTests.cs`:

```csharp
using System.Text.Json;
using OpenAgent.LlmVoice.OpenAIAzure;
using OpenAgent.Models.Conversations;
using OpenAgent.Models.Voice;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace OpenAgent.Tests;

public class AzureOpenAiVoiceProviderOptionsTests
{
    [Fact]
    public void StartSessionAsync_WithUlawOptions_Throws_NotConfigured_NotCodecError()
    {
        // Provider not configured — we just want to confirm the new signature compiles
        // and the options parameter is accepted without throwing on argument validation.
        var provider = new AzureOpenAiRealtimeVoiceProvider(
            agentLogic: null!,
            logger: NullLogger<AzureOpenAiRealtimeVoiceProvider>.Instance);

        var conversation = new Conversation
        {
            Id = "c1",
            Source = "test",
            Type = ConversationType.Phone,
            Provider = "azure-openai-voice",
            Model = "gpt-realtime"
        };

        var options = new VoiceSessionOptions("g711_ulaw", 8000);

        var ex = Assert.ThrowsAsync<InvalidOperationException>(() =>
            provider.StartSessionAsync(conversation, options));
        Assert.Contains("not been configured", ex.Result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ConfigFields_DoesNotIncludeCodecOrSampleRate()
    {
        var provider = new AzureOpenAiRealtimeVoiceProvider(
            agentLogic: null!,
            logger: NullLogger<AzureOpenAiRealtimeVoiceProvider>.Instance);

        Assert.DoesNotContain(provider.ConfigFields, f => f.Key == "codec");
        Assert.DoesNotContain(provider.ConfigFields, f => f.Key == "sampleRate");
    }
}
```

- [ ] **Step 2: Run the test, expect FAIL.**

```bash
cd src/agent && dotnet test OpenAgent.Tests/OpenAgent.Tests.csproj --filter FullyQualifiedName~AzureOpenAiVoiceProviderOptionsTests
```

Expected: COMPILE FAIL or test FAIL — the provider still has codec config field and the new signature isn't accepted yet.

- [ ] **Step 3: Update `AzureOpenAiRealtimeVoiceProvider.cs`.**

Remove the `codec` `ProviderConfigField` entry. Update the `StartSessionAsync` method to match the new signature. The field that holds `Codec` on `_config` becomes unused for the provider-level default; remove it from `RealtimeConfig` (or leave but ignore — prefer remove for cleanliness).

```csharp
public IReadOnlyList<ProviderConfigField> ConfigFields { get; } =
[
    new() { Key = "endpoint", Label = "Azure OpenAI Endpoint", Type = "String", Required = true },
    new() { Key = "apiKey",   Label = "API Key",                Type = "Secret", Required = true },
    new() { Key = "models",   Label = "Models (comma-separated)", Type = "String", Required = true,
        DefaultValue = "gpt-realtime" },
    new() { Key = "voice",    Label = "Voice", Type = "Enum", DefaultValue = "alloy",
        Options = ["alloy","ash","ballad","coral","echo","sage","shimmer","verse"] },
];

public async Task<IVoiceSession> StartSessionAsync(
    Conversation conversation,
    VoiceSessionOptions? options = null,
    CancellationToken ct = default)
{
    if (_config is null)
        throw new InvalidOperationException("Provider has not been configured. Call Configure() first.");

    var session = new AzureOpenAiVoiceSession(_config, conversation, agentLogic, options, logger);
    await session.ConnectAsync(ct);
    return session;
}
```

- [ ] **Step 4: Update `AzureOpenAiVoiceSession.cs`.**

Constructor accepts `VoiceSessionOptions?`. The session uses options first, falls back to `pcm16 24000`. Replace the `_config.Codec` read at line 130 with options:

```csharp
internal sealed class AzureOpenAiVoiceSession : IVoiceSession
{
    private readonly RealtimeConfig _config;
    private readonly Conversation _conversation;
    private readonly IAgentLogic _agentLogic;
    private readonly ILogger _logger;
    private readonly string _codec;
    private readonly int _sampleRate;
    // ... existing fields ...

    public AzureOpenAiVoiceSession(
        RealtimeConfig config,
        Conversation conversation,
        IAgentLogic agentLogic,
        VoiceSessionOptions? options,
        ILogger logger)
    {
        _config = config;
        _conversation = conversation;
        _agentLogic = agentLogic;
        _logger = logger;

        // Default to pcm16 24 kHz when no options supplied. Validate sample rate matches Azure's
        // hard-coded mapping per codec; reject mismatches rather than silently downgrading.
        var requested = options ?? new VoiceSessionOptions("pcm16", 24000);
        var expectedRate = RateForCodec(requested.Codec);
        if (requested.SampleRate != expectedRate)
            throw new ArgumentException(
                $"Azure Realtime supports {requested.Codec} only at {expectedRate} Hz, got {requested.SampleRate}.",
                nameof(options));
        _codec = requested.Codec;
        _sampleRate = requested.SampleRate;
    }
```

In the session-update payload (around line 130), replace `var codec = string.IsNullOrWhiteSpace(_config.Codec) ? "pcm16" : _config.Codec!;` with `var codec = _codec;`. Same for the `RateForCodec` call.

Remove `Codec` from `RealtimeConfig` (or leave it inert — but remove for clarity).

- [ ] **Step 5: Run the new test, expect PASS.**

```bash
dotnet test OpenAgent.Tests/OpenAgent.Tests.csproj --filter FullyQualifiedName~AzureOpenAiVoiceProviderOptionsTests
```

Expected: 2 passed.

- [ ] **Step 6: Run all tests; expect compilation issues elsewhere (Grok, Gemini, VoiceSessionManager) to remain — they get fixed in Tasks 3–4.**

```bash
dotnet build
```

Expected: errors in Grok, Gemini, and the host's `VoiceSessionManager` (it calls `StartSessionAsync` and now needs to pass null or an options value).

- [ ] **Step 7: Update `VoiceSessionManager` call site to pass `null` options (host doesn't override; keeps default).**

Find the call:

```bash
grep -rn 'StartSessionAsync' OpenAgent/ | head -5
```

Update the call: `await provider.StartSessionAsync(conversation, options: null, ct);` (or just rely on the default value).

- [ ] **Step 8: Commit.**

```bash
git add src/agent/OpenAgent.LlmVoice.OpenAIAzure/ src/agent/OpenAgent.Tests/AzureOpenAiVoiceProviderOptionsTests.cs src/agent/OpenAgent/VoiceSessionManager.cs
git commit -m "refactor(voice): Azure Realtime honours VoiceSessionOptions, drop codec config field"
```

---

## Task 3: Update Grok Realtime voice provider for VoiceSessionOptions

**Files:**

- Modify: `src/agent/OpenAgent.LlmVoice.GrokRealtime/GrokRealtimeVoiceProvider.cs`
- Modify: `src/agent/OpenAgent.LlmVoice.GrokRealtime/GrokVoiceSession.cs`

- [ ] **Step 1: Mirror Task 2's test under `OpenAgent.Tests/GrokRealtimeVoiceProviderOptionsTests.cs`.**

```csharp
using OpenAgent.LlmVoice.GrokRealtime;
using OpenAgent.Models.Conversations;
using OpenAgent.Models.Voice;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace OpenAgent.Tests;

public class GrokRealtimeVoiceProviderOptionsTests
{
    [Fact]
    public async Task StartSessionAsync_WithUlawOptions_Throws_NotConfigured_NotCodecError()
    {
        var provider = new GrokRealtimeVoiceProvider(
            agentLogic: null!,
            logger: NullLogger<GrokRealtimeVoiceProvider>.Instance);

        var conversation = new Conversation
        {
            Id = "c1", Source = "test", Type = ConversationType.Voice,
            Provider = "grok-realtime-voice", Model = "grok-3-realtime"
        };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            provider.StartSessionAsync(conversation, new VoiceSessionOptions("g711_ulaw", 8000)));
        Assert.Contains("not been configured", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ConfigFields_DoesNotIncludeCodecOrSampleRate()
    {
        var provider = new GrokRealtimeVoiceProvider(
            agentLogic: null!,
            logger: NullLogger<GrokRealtimeVoiceProvider>.Instance);

        Assert.DoesNotContain(provider.ConfigFields, f => f.Key == "codec");
        Assert.DoesNotContain(provider.ConfigFields, f => f.Key == "sampleRate");
    }
}
```

- [ ] **Step 2: Run the test, expect FAIL.**

```bash
dotnet test --filter FullyQualifiedName~GrokRealtimeVoiceProviderOptionsTests
```

- [ ] **Step 3: Update `GrokRealtimeVoiceProvider.cs`.**

Remove `codec` and `sampleRate` from `ConfigFields`. Update `StartSessionAsync` signature and pass options through to the session constructor:

```csharp
public IReadOnlyList<ProviderConfigField> ConfigFields { get; } =
[
    new() { Key = "apiKey", Label = "API Key", Type = "Secret", Required = true },
    new() { Key = "models", Label = "Models (comma-separated)", Type = "String", Required = true,
        DefaultValue = "grok-3-realtime" },
    new() { Key = "voice",  Label = "Voice", Type = "Enum", DefaultValue = "Sol",
        Options = ["Sol","Atlas","Nova","Sage","Aurora"] },
];

public async Task<IVoiceSession> StartSessionAsync(
    Conversation conversation,
    VoiceSessionOptions? options = null,
    CancellationToken ct = default)
{
    if (_config is null)
        throw new InvalidOperationException("Provider has not been configured. Call Configure() first.");

    var session = new GrokVoiceSession(_config, conversation, agentLogic, options, logger);
    await session.ConnectAsync(ct);
    return session;
}
```

Remove `Codec` and `SampleRate` properties from `GrokConfig` (or leave inert — prefer remove).

- [ ] **Step 4: Update `GrokVoiceSession.cs`.**

Add the codec/rate fields and constructor parameter. Replace `_config.Codec` reads with the local fields. Validate per Grok's accepted codecs (`pcm16`, `g711_ulaw`, `g711_alaw`); for `pcm16` Grok accepts a configurable rate but standardise on `24000` to match Azure default.

```csharp
private readonly string _codec;
private readonly int _sampleRate;

public GrokVoiceSession(
    GrokConfig config,
    Conversation conversation,
    IAgentLogic agentLogic,
    VoiceSessionOptions? options,
    ILogger logger)
{
    _config = config;
    _conversation = conversation;
    _agentLogic = agentLogic;
    _logger = logger;

    var requested = options ?? new VoiceSessionOptions("pcm16", 24000);
    if (requested.Codec is "g711_ulaw" or "g711_alaw" && requested.SampleRate != 8000)
        throw new ArgumentException("g711_* requires 8000 Hz", nameof(options));
    if (requested.Codec is "pcm16" && requested.SampleRate is not (8000 or 16000 or 24000))
        throw new ArgumentException("pcm16 supports 8000/16000/24000 Hz", nameof(options));

    _codec = requested.Codec;
    _sampleRate = requested.SampleRate;
}
```

Then replace any `_config.Codec` / `_config.SampleRate` with `_codec` / `_sampleRate`. Inspect `GrokVoiceSession.cs` lines 161–181 (the `EncodingFor` and `RateFor` helpers) and route them through the local fields.

- [ ] **Step 5: Run the test, expect PASS.**

```bash
dotnet test --filter FullyQualifiedName~GrokRealtimeVoiceProviderOptionsTests
```

Expected: 2 passed.

- [ ] **Step 6: Commit.**

```bash
git add src/agent/OpenAgent.LlmVoice.GrokRealtime/ src/agent/OpenAgent.Tests/GrokRealtimeVoiceProviderOptionsTests.cs
git commit -m "refactor(voice): Grok Realtime honours VoiceSessionOptions, drop codec/sampleRate config fields"
```

---

## Task 4: Update Gemini Live voice provider signature only (no behaviour change)

Gemini Live has hardcoded PCM16 16k in / 24k out and no codec config field today. We update only the method signature so it conforms to the modified interface; behaviour is unchanged. Phone support for Gemini is explicitly out of scope.

**Files:**

- Modify: `src/agent/OpenAgent.LlmVoice.GeminiLive/GeminiLiveVoiceProvider.cs`

- [ ] **Step 1: Update the `StartSessionAsync` signature.**

```csharp
public async Task<IVoiceSession> StartSessionAsync(
    Conversation conversation,
    VoiceSessionOptions? options = null,
    CancellationToken ct = default)
{
    if (_config is null)
        throw new InvalidOperationException("Provider has not been configured. Call Configure() first.");

    // Gemini hardcodes its audio formats — reject any options that don't match.
    if (options is not null && (options.Codec != "pcm16" || options.SampleRate != 16000))
        throw new ArgumentException(
            "Gemini Live only supports pcm16 16000 Hz input today. Use Azure or Grok for other formats.",
            nameof(options));

    logger.LogDebug("Starting Gemini Live session for conversation {ConversationId}", conversation.Id);
    var session = new GeminiLiveVoiceSession(_config, conversation, agentLogic, logger);
    await session.ConnectAsync(ct);
    return session;
}
```

- [ ] **Step 2: Build the solution to confirm the interface contract is fully satisfied.**

```bash
dotnet build
```

Expected: build succeeds across the solution. All three providers now implement the new contract.

- [ ] **Step 3: Run the full test suite to confirm no regressions.**

```bash
dotnet test
```

Expected: all existing tests pass, plus the two new option tests from Tasks 2–3.

- [ ] **Step 4: Commit.**

```bash
git add src/agent/OpenAgent.LlmVoice.GeminiLive/GeminiLiveVoiceProvider.cs
git commit -m "refactor(voice): Gemini Live conforms to new ILlmVoiceProvider signature"
```

---

## Task 5: Add ConversationType.Phone + PHONE.md system prompt

**Files:**

- Modify: `src/agent/OpenAgent.Models/Conversations/Conversation.cs`
- Create: `src/agent/OpenAgent/defaults/PHONE.md`
- Modify: `src/agent/OpenAgent/SystemPromptBuilder.cs`
- Test: `src/agent/OpenAgent.Tests/SystemPromptBuilderTests.cs` (existing)

- [ ] **Step 1: Write the failing test for `PHONE.md` selection.**

Open `src/agent/OpenAgent.Tests/SystemPromptBuilderTests.cs` and append:

```csharp
[Fact]
public void Build_PhoneType_IncludesPhoneMd_AndCommonFiles()
{
    using var temp = new TempDataDir();
    File.WriteAllText(Path.Combine(temp.Path, "AGENTS.md"),  "AGENTS-content");
    File.WriteAllText(Path.Combine(temp.Path, "SOUL.md"),    "SOUL-content");
    File.WriteAllText(Path.Combine(temp.Path, "IDENTITY.md"),"IDENTITY-content");
    File.WriteAllText(Path.Combine(temp.Path, "USER.md"),    "USER-content");
    File.WriteAllText(Path.Combine(temp.Path, "TOOLS.md"),   "TOOLS-content");
    File.WriteAllText(Path.Combine(temp.Path, "MEMORY.md"),  "MEMORY-content");
    File.WriteAllText(Path.Combine(temp.Path, "VOICE.md"),   "VOICE-content");
    File.WriteAllText(Path.Combine(temp.Path, "PHONE.md"),   "PHONE-content");

    var builder = new SystemPromptBuilder(temp.Path, NullLogger<SystemPromptBuilder>.Instance);
    var prompt = builder.Build("test", ConversationType.Phone, [], intention: null);

    Assert.Contains("AGENTS-content", prompt);
    Assert.Contains("SOUL-content", prompt);
    Assert.Contains("PHONE-content", prompt);
    Assert.DoesNotContain("VOICE-content", prompt); // VOICE.md is for ConversationType.Voice only
}
```

(`TempDataDir` is the existing test helper in the suite — search for it to confirm its shape; if absent use `Path.GetTempPath()` + cleanup.)

- [ ] **Step 2: Run the test, expect compile failure on `ConversationType.Phone`.**

```bash
dotnet test --filter Build_PhoneType_IncludesPhoneMd
```

Expected: compile error — `Phone` is not a member of `ConversationType`.

- [ ] **Step 3: Add the enum value.**

In `src/agent/OpenAgent.Models/Conversations/Conversation.cs`:

```csharp
public enum ConversationType
{
    Text,
    Voice,
    Phone,
}
```

- [ ] **Step 4: Create `defaults/PHONE.md`.**

Write `src/agent/OpenAgent/defaults/PHONE.md`:

```markdown
# Phone Call Etiquette

You are speaking with a caller over a regular phone call. The audio
goes through Telnyx's Media Streaming WebSocket; their carrier converts
between PSTN and µ-law 8 kHz on our wire.

**Keep replies short.** One or two sentences per turn. Long paragraphs
sound robotic when read aloud and waste the caller's time.

**Speak naturally.** Avoid bullet lists, code blocks, or markdown
headings — they will be spoken verbatim and sound odd. Prefer full
sentences.

**You can hang up.** When the caller signals goodbye, thanks, or "that's
all", call the `end_call` tool to drop the line politely. Don't keep the
conversation going after a clear closing.

**Watch for silence.** If the caller's transcript comes through as empty
or unclear, prompt them with a short question rather than guessing.

**You cannot see anything.** No screen, no images, no files visible to
the caller. Do not offer to "show" or "display" anything.

**Tools take time.** When you call a tool that takes more than a moment
(web fetch, search), the caller will hear a short ambient sound while
you work. Carry on naturally when the tool returns.
```

- [ ] **Step 5: Update `SystemPromptBuilder.FileMap`.**

Open `src/agent/OpenAgent/SystemPromptBuilder.cs` and update the `FileMap` to include `Phone` in the existing rows AND add the new `PHONE.md` row. The existing structure (after master) typically looks like:

```csharp
private static readonly (string FileName, ConversationType[] Types)[] FileMap =
[
    ("AGENTS.md",   [ConversationType.Text, ConversationType.Voice, ConversationType.Phone]),
    ("SOUL.md",     [ConversationType.Text, ConversationType.Voice, ConversationType.Phone]),
    ("IDENTITY.md", [ConversationType.Text, ConversationType.Voice, ConversationType.Phone]),
    ("USER.md",     [ConversationType.Text, ConversationType.Voice, ConversationType.Phone]),
    ("TOOLS.md",    [ConversationType.Text, ConversationType.Voice, ConversationType.Phone]),
    ("MEMORY.md",   [ConversationType.Text, ConversationType.Voice, ConversationType.Phone]),
    ("VOICE.md",    [ConversationType.Voice]),
    ("PHONE.md",    [ConversationType.Phone]),
];
```

(If `FileMap` differs in master, adapt the same shape.)

- [ ] **Step 6: Run the test, expect PASS.**

```bash
dotnet test --filter Build_PhoneType_IncludesPhoneMd
```

Expected: pass.

- [ ] **Step 7: Run the full suite.**

```bash
dotnet test
```

Expected: all green. The `PHONE.md` file is auto-extracted by `DataDirectoryBootstrap` because of the existing embedded-resource scan (any `.md` under `defaults/`).

- [ ] **Step 8: Commit.**

```bash
git add src/agent/OpenAgent.Models/Conversations/Conversation.cs \
        src/agent/OpenAgent/defaults/PHONE.md \
        src/agent/OpenAgent/SystemPromptBuilder.cs \
        src/agent/OpenAgent.Tests/SystemPromptBuilderTests.cs
git commit -m "feat(conversation): add Phone type with dedicated PHONE.md system prompt"
```

---

## Task 6: Browser thinking-cue events on the existing voice WebSocket

**Files:**

- Modify: `src/agent/OpenAgent.Models/Voice/VoiceWebSocketEvents.cs` (or wherever the existing voice WS event types live — `grep -n VoiceWebSocketEvent OpenAgent.Models/`).
- Modify: `src/agent/OpenAgent.Api/Endpoints/WebSocketVoiceEndpoints.cs`
- Test: append to existing `OpenAgent.Tests/WebSocketVoiceEndpointsTests.cs` (create the file if it doesn't exist)

- [ ] **Step 1: Add the new event records.**

In `OpenAgent.Models/Voice/VoiceWebSocketEvents.cs`:

```csharp
public sealed class VoiceThinkingStartedEvent
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = "thinking_started";
}

public sealed class VoiceThinkingStoppedEvent
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = "thinking_stopped";
}
```

If the file doesn't already use `[JsonPropertyName]`, follow whatever attribute style is in use in that file.

- [ ] **Step 2: Update `WebSocketVoiceEndpoints.RunBridgeAsync` write loop to emit the events.**

Look at `OpenAgent.Api/Endpoints/WebSocketVoiceEndpoints.cs` around lines 119–182 and add two cases to the switch in `WriteLoopAsync`:

```csharp
case VoiceToolCallStarted:
    await SendJsonAsync(ws, new VoiceThinkingStartedEvent(), ct);
    break;

case VoiceToolCallCompleted:
    await SendJsonAsync(ws, new VoiceThinkingStoppedEvent(), ct);
    break;
```

(`VoiceToolCallStarted`/`VoiceToolCallCompleted` were added in Task 6. If you find that they're called something else in the codebase, the names below may have drifted — match what's actually there.

```csharp
public sealed record VoiceToolCallStarted(string Name, string CallId) : VoiceEvent;
public sealed record VoiceToolCallCompleted(string CallId, string Result) : VoiceEvent;
```

and have the existing voice sessions emit them when they invoke tools — search for `ExecuteToolAsync` in `AzureOpenAiVoiceSession.cs` and emit the events around the call.)

- [ ] **Step 3: Write a test that drives the bridge with a fake voice session and asserts the events round-trip.**

```csharp
// OpenAgent.Tests/VoiceThinkingCueTests.cs
using System.Text.Json;
using OpenAgent.Models.Voice;
using Xunit;

namespace OpenAgent.Tests;

public class VoiceThinkingCueTests
{
    [Fact]
    public void ThinkingStartedEvent_Serializes_As_thinking_started()
    {
        var json = JsonSerializer.Serialize(new VoiceThinkingStartedEvent());
        Assert.Contains("\"type\":\"thinking_started\"", json);
    }

    [Fact]
    public void ThinkingStoppedEvent_Serializes_As_thinking_stopped()
    {
        var json = JsonSerializer.Serialize(new VoiceThinkingStoppedEvent());
        Assert.Contains("\"type\":\"thinking_stopped\"", json);
    }
}
```

Integration tests across the WS bridge are deferred to Task 21 (where the same logic lives in `TelnyxMediaBridge`); for the browser path the endpoint mod is small enough to verify by hand once Task 25 manual testing happens.

- [ ] **Step 4: Run tests, expect PASS.**

```bash
dotnet test --filter VoiceThinkingCueTests
```

- [ ] **Step 5: Commit.**

```bash
git add src/agent/OpenAgent.Models/Voice/VoiceWebSocketEvents.cs \
        src/agent/OpenAgent.Models/Voice/VoiceEvents.cs \
        src/agent/OpenAgent.Api/Endpoints/WebSocketVoiceEndpoints.cs \
        src/agent/OpenAgent.LlmVoice.OpenAIAzure/AzureOpenAiVoiceSession.cs \
        src/agent/OpenAgent.LlmVoice.GrokRealtime/GrokVoiceSession.cs \
        src/agent/OpenAgent.Tests/VoiceThinkingCueTests.cs
git commit -m "feat(voice): emit thinking_started/stopped events on tool boundary"
```

---

## Task 7: Browser endpoint passes null options (no behaviour change, defensive cleanup)

**Files:**

- Modify: `src/agent/OpenAgent.Api/Endpoints/WebSocketVoiceEndpoints.cs`

- [ ] **Step 1: Inspect how the browser endpoint currently calls into the session manager.**

```bash
grep -n 'GetOrCreateSession\|StartSessionAsync' OpenAgent/VoiceSessionManager.cs OpenAgent.Api/Endpoints/WebSocketVoiceEndpoints.cs
```

The browser path goes through `IVoiceSessionManager.GetOrCreateSessionAsync` which internally calls `provider.StartSessionAsync(conversation, ...)`. We want this internal call to pass `options: null` so the provider falls back to its default (`pcm16 24000`).

- [ ] **Step 2: Add an explicit `null` to the `StartSessionAsync` call inside `VoiceSessionManager`.**

The change should be a no-op behaviourally — confirms intent at the call site. Skip this task if the call already reads `provider.StartSessionAsync(conversation, ct)` (default value handles it). Add a code comment explaining the implicit default.

- [ ] **Step 3: Build + test.**

```bash
dotnet build && dotnet test
```

- [ ] **Step 4: Commit (or skip if no change).**

```bash
git add src/agent/OpenAgent/VoiceSessionManager.cs
git commit -m "chore(voice): document browser endpoint relies on provider default codec"
```

> **PR1 boundary.** Tasks 1–7 form a self-contained refactor. Browser voice still works, browser thinking cues land. Open a PR or merge before continuing.

---

## Task 8: Create OpenAgent.Channel.Telnyx project skeleton

**Files:**

- Create: `src/agent/OpenAgent.Channel.Telnyx/OpenAgent.Channel.Telnyx.csproj`

- [ ] **Step 1: Create the project.**

```bash
cd src/agent
dotnet new classlib --name OpenAgent.Channel.Telnyx --output OpenAgent.Channel.Telnyx --framework net10.0
rm OpenAgent.Channel.Telnyx/Class1.cs
```

Replace the generated csproj with:

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
    <PackageReference Include="BouncyCastle.Cryptography" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\OpenAgent.Contracts\OpenAgent.Contracts.csproj" />
    <ProjectReference Include="..\OpenAgent.Models\OpenAgent.Models.csproj" />
  </ItemGroup>
</Project>
```

- [ ] **Step 2: Add to the solution.**

```bash
cd src/agent
dotnet sln add OpenAgent.Channel.Telnyx/OpenAgent.Channel.Telnyx.csproj --solution-folder Channels
```

- [ ] **Step 3: Reference from the host and tests project.**

```bash
dotnet add OpenAgent/OpenAgent.csproj reference OpenAgent.Channel.Telnyx/OpenAgent.Channel.Telnyx.csproj
dotnet add OpenAgent.Tests/OpenAgent.Tests.csproj reference OpenAgent.Channel.Telnyx/OpenAgent.Channel.Telnyx.csproj
```

- [ ] **Step 4: Build.**

```bash
dotnet build
```

Expected: succeeds; new project is empty.

- [ ] **Step 5: Commit.**

```bash
git add src/agent/OpenAgent.Channel.Telnyx/ src/agent/OpenAgent.sln src/agent/OpenAgent/OpenAgent.csproj src/agent/OpenAgent.Tests/OpenAgent.Tests.csproj
git commit -m "feat(telnyx): add empty Channel.Telnyx project skeleton"
```

---

## Task 9: TelnyxOptions

**Files:**

- Create: `src/agent/OpenAgent.Channel.Telnyx/TelnyxOptions.cs`

- [ ] **Step 1: Write the options class.**

```csharp
namespace OpenAgent.Channel.Telnyx;

/// <summary>
/// Strongly-typed configuration for a Telnyx Media Streaming connection. Populated by
/// <see cref="TelnyxChannelProviderFactory.Create"/> from the connection's JsonElement config blob.
/// </summary>
public sealed class TelnyxOptions
{
    /// <summary>Telnyx API key (v2). Used as Authorization: Bearer for Call Control REST commands.</summary>
    public string? ApiKey { get; set; }

    /// <summary>The E.164 phone number this connection owns (e.g. "+4535150636"). Cosmetic; routing is on Telnyx side.</summary>
    public string? PhoneNumber { get; set; }

    /// <summary>Telnyx connection ID of the Call Control connection routing the number. Used to validate webhook payloads.</summary>
    public string? CallControlAppId { get; set; }

    /// <summary>Public HTTPS URL of this OpenAgent instance. Webhook + WebSocket URLs derive from it.</summary>
    public string? BaseUrl { get; set; }

    /// <summary>PEM-encoded ED25519 public key from the Telnyx Developer Hub. When blank, signature verification is SKIPPED with a warning (dev only).</summary>
    public string? WebhookPublicKey { get; set; }

    /// <summary>E.164 numbers allowed to call. Empty list = allow all.</summary>
    public List<string> AllowedNumbers { get; set; } = [];

    /// <summary>Optional path under dataPath to a custom µ-law 8 kHz mono thinking clip. Falls back to the procedural default.</summary>
    public string? ThinkingClipPath { get; set; }

    /// <summary>Auto-generated 12-hex GUID identifying this connection's webhook URLs. Populated on first start; persisted to connections.json.</summary>
    public string? WebhookId { get; set; }
}
```

- [ ] **Step 2: Build.**

```bash
dotnet build OpenAgent.Channel.Telnyx/OpenAgent.Channel.Telnyx.csproj
```

- [ ] **Step 3: Commit.**

```bash
git add src/agent/OpenAgent.Channel.Telnyx/TelnyxOptions.cs
git commit -m "feat(telnyx): TelnyxOptions config class"
```

---

## Task 10: TelnyxSignatureVerifier (port from P2 branch)

The verifier is unchanged from the prior P2 work. We could `git show feature/telnyx-channel-scaffolding:src/agent/OpenAgent.Channel.Telnyx/TelnyxSignatureVerifier.cs > .../TelnyxSignatureVerifier.cs` to recover it verbatim. Then port the test.

**Files:**

- Create: `src/agent/OpenAgent.Channel.Telnyx/TelnyxSignatureVerifier.cs`
- Create: `src/agent/OpenAgent.Tests/TelnyxSignatureVerifierTests.cs`

- [ ] **Step 1: Write the failing test first — drives the API shape.**

```csharp
// OpenAgent.Tests/TelnyxSignatureVerifierTests.cs
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using OpenAgent.Channel.Telnyx;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using Org.BouncyCastle.OpenSsl;
using Org.BouncyCastle.Security;
using Xunit;

namespace OpenAgent.Tests;

public class TelnyxSignatureVerifierTests
{
    [Fact]
    public void Verify_ValidSignature_ReturnsTrue()
    {
        var (publicPem, privateKey) = GenerateEd25519KeyPair();
        var ts = "1700000000";
        var body = "test=body"u8.ToArray();
        var signature = SignTimestampPlusBody(privateKey, ts, body);
        var sigB64 = Convert.ToBase64String(signature);

        var verifier = new TelnyxSignatureVerifier(NullLogger<TelnyxSignatureVerifier>.Instance);
        var ok = verifier.Verify(publicPem, sigB64, ts, body, DateTimeOffset.FromUnixTimeSeconds(1700000000));

        Assert.True(ok);
    }

    [Fact]
    public void Verify_NoPublicKey_LogsWarning_ReturnsTrue()
    {
        var verifier = new TelnyxSignatureVerifier(NullLogger<TelnyxSignatureVerifier>.Instance);
        var ok = verifier.Verify(null, "sig", "ts", "body"u8.ToArray(), DateTimeOffset.UtcNow);
        Assert.True(ok);
    }

    [Fact]
    public void Verify_ExpiredTimestamp_ReturnsFalse()
    {
        var (publicPem, privateKey) = GenerateEd25519KeyPair();
        var ts = "1700000000";
        var body = "x"u8.ToArray();
        var sig = Convert.ToBase64String(SignTimestampPlusBody(privateKey, ts, body));

        var verifier = new TelnyxSignatureVerifier(NullLogger<TelnyxSignatureVerifier>.Instance);
        var ok = verifier.Verify(publicPem, sig, ts, body, DateTimeOffset.FromUnixTimeSeconds(1700001000)); // 1000s skew
        Assert.False(ok);
    }

    [Fact]
    public void Verify_TamperedBody_ReturnsFalse()
    {
        var (publicPem, privateKey) = GenerateEd25519KeyPair();
        var ts = "1700000000";
        var sig = Convert.ToBase64String(SignTimestampPlusBody(privateKey, ts, "good"u8.ToArray()));
        var verifier = new TelnyxSignatureVerifier(NullLogger<TelnyxSignatureVerifier>.Instance);
        Assert.False(verifier.Verify(publicPem, sig, ts, "tampered"u8.ToArray(), DateTimeOffset.FromUnixTimeSeconds(1700000000)));
    }

    [Fact]
    public void Verify_MalformedKey_ReturnsFalse()
    {
        var verifier = new TelnyxSignatureVerifier(NullLogger<TelnyxSignatureVerifier>.Instance);
        Assert.False(verifier.Verify("not-a-pem", "sig", "1700000000", "x"u8.ToArray(), DateTimeOffset.FromUnixTimeSeconds(1700000000)));
    }

    private static (string PublicPem, Ed25519PrivateKeyParameters Private) GenerateEd25519KeyPair()
    {
        var generator = new Ed25519KeyPairGenerator();
        generator.Init(new Ed25519KeyGenerationParameters(new SecureRandom()));
        var pair = generator.GenerateKeyPair();
        var pub = (Ed25519PublicKeyParameters)pair.Public;
        var priv = (Ed25519PrivateKeyParameters)pair.Private;

        using var sw = new StringWriter();
        var pem = new PemWriter(sw);
        pem.WriteObject(pub);
        pem.Writer.Flush();
        return (sw.ToString(), priv);
    }

    private static byte[] SignTimestampPlusBody(Ed25519PrivateKeyParameters key, string timestamp, byte[] body)
    {
        var prefix = Encoding.UTF8.GetBytes(timestamp + "|");
        var payload = new byte[prefix.Length + body.Length];
        Buffer.BlockCopy(prefix, 0, payload, 0, prefix.Length);
        Buffer.BlockCopy(body, 0, payload, prefix.Length, body.Length);

        var signer = new Ed25519Signer();
        signer.Init(forSigning: true, key);
        signer.BlockUpdate(payload, 0, payload.Length);
        return signer.GenerateSignature();
    }
}
```

- [ ] **Step 2: Run, expect FAIL (no class).**

```bash
dotnet test --filter TelnyxSignatureVerifierTests
```

- [ ] **Step 3: Recover the verifier from the frozen P2 branch.**

```bash
cd ../..  # repo root
git show feature/telnyx-channel-scaffolding:src/agent/OpenAgent.Channel.Telnyx/TelnyxSignatureVerifier.cs > src/agent/OpenAgent.Channel.Telnyx/TelnyxSignatureVerifier.cs
```

If the diff shows ASCII-vs-UTF8 BOM or CRLF noise, normalise. Otherwise the file is ready.

- [ ] **Step 4: Run tests, expect PASS.**

```bash
cd src/agent && dotnet test --filter TelnyxSignatureVerifierTests
```

Expected: all 5 tests pass.

- [ ] **Step 5: Commit.**

```bash
git add src/agent/OpenAgent.Channel.Telnyx/TelnyxSignatureVerifier.cs src/agent/OpenAgent.Tests/TelnyxSignatureVerifierTests.cs
git commit -m "feat(telnyx): ED25519 signature verifier (port of P2)"
```

---

## Task 11: TelnyxMediaFrame parsing

The Telnyx WS protocol frame shapes (verbatim from their docs):

- Inbound:
  - `{"event":"start","sequence_number":"1","start":{"call_control_id":"...","client_state":"...","media_format":{"encoding":"PCMU","sample_rate":8000,"channels":1}},"stream_id":"..."}`
  - `{"event":"media","sequence_number":"4","media":{"track":"inbound","chunk":"2","timestamp":"...","payload":"<base64>"},"stream_id":"..."}`
  - `{"event":"stop","sequence_number":"99","stop":{"reason":"..."}}`
  - `{"event":"dtmf","dtmf":{"digit":"5"}}` (parsed but ignored)
- Outbound:
  - `{"event":"media","media":{"payload":"<base64>"}}`
  - `{"event":"clear"}`

**Files:**

- Create: `src/agent/OpenAgent.Channel.Telnyx/TelnyxMediaFrame.cs`
- Test: `src/agent/OpenAgent.Tests/TelnyxMediaFrameTests.cs`

- [ ] **Step 1: Write the failing tests.**

```csharp
// OpenAgent.Tests/TelnyxMediaFrameTests.cs
using System.Text.Json;
using OpenAgent.Channel.Telnyx;
using Xunit;

namespace OpenAgent.Tests;

public class TelnyxMediaFrameTests
{
    [Fact]
    public void Parse_StartEvent()
    {
        var json = """{"event":"start","sequence_number":"1","start":{"call_control_id":"call-123","client_state":"YwAxMjM=","media_format":{"encoding":"PCMU","sample_rate":8000,"channels":1}},"stream_id":"s1"}""";
        var frame = TelnyxMediaFrame.Parse(json);
        Assert.Equal("start", frame.Event);
        Assert.Equal("call-123", frame.Start!.CallControlId);
        Assert.Equal("PCMU", frame.Start.MediaFormat.Encoding);
        Assert.Equal(8000, frame.Start.MediaFormat.SampleRate);
    }

    [Fact]
    public void Parse_MediaEvent_InboundTrack()
    {
        var json = """{"event":"media","sequence_number":"4","media":{"track":"inbound","chunk":"2","timestamp":"123","payload":"AAEC"}}""";
        var frame = TelnyxMediaFrame.Parse(json);
        Assert.Equal("media", frame.Event);
        Assert.Equal("inbound", frame.Media!.Track);
        Assert.Equal(new byte[] { 0x00, 0x01, 0x02 }, frame.Media.PayloadBytes);
    }

    [Fact]
    public void Parse_MediaEvent_OutboundTrack_ParsesButCallerShouldFilter()
    {
        var json = """{"event":"media","media":{"track":"outbound","payload":"AA=="}}""";
        var frame = TelnyxMediaFrame.Parse(json);
        Assert.Equal("outbound", frame.Media!.Track);
    }

    [Fact]
    public void Parse_StopEvent()
    {
        var json = """{"event":"stop","stop":{"reason":"hangup"}}""";
        var frame = TelnyxMediaFrame.Parse(json);
        Assert.Equal("stop", frame.Event);
    }

    [Fact]
    public void Parse_DtmfEvent_ParsedNotIgnored()
    {
        var json = """{"event":"dtmf","dtmf":{"digit":"5"}}""";
        var frame = TelnyxMediaFrame.Parse(json);
        Assert.Equal("dtmf", frame.Event);
        Assert.Equal("5", frame.Dtmf!.Digit);
    }

    [Fact]
    public void Compose_MediaFrame_EncodesPayload()
    {
        var json = TelnyxMediaFrame.ComposeMedia(new byte[] { 0xff, 0xfe });
        var doc = JsonDocument.Parse(json);
        Assert.Equal("media", doc.RootElement.GetProperty("event").GetString());
        Assert.Equal("//4=", doc.RootElement.GetProperty("media").GetProperty("payload").GetString());
    }

    [Fact]
    public void Compose_ClearFrame()
    {
        var json = TelnyxMediaFrame.ComposeClear();
        Assert.Equal("""{"event":"clear"}""", json);
    }
}
```

- [ ] **Step 2: Run, expect FAIL.**

```bash
dotnet test --filter TelnyxMediaFrameTests
```

- [ ] **Step 3: Implement `TelnyxMediaFrame`.**

```csharp
// OpenAgent.Channel.Telnyx/TelnyxMediaFrame.cs
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenAgent.Channel.Telnyx;

/// <summary>
/// Wire-level JSON envelope for Telnyx Media Streaming events. Handles parse and compose for the
/// minimal set used by the bridge: start/media/stop/dtmf inbound, media/clear outbound.
/// </summary>
public sealed class TelnyxMediaFrame
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    [JsonPropertyName("event")] public string Event { get; init; } = "";
    [JsonPropertyName("sequence_number")] public string? SequenceNumber { get; init; }
    [JsonPropertyName("stream_id")] public string? StreamId { get; init; }
    [JsonPropertyName("start")] public StartPayload? Start { get; init; }
    [JsonPropertyName("media")] public MediaPayload? Media { get; init; }
    [JsonPropertyName("stop")] public StopPayload? Stop { get; init; }
    [JsonPropertyName("dtmf")] public DtmfPayload? Dtmf { get; init; }

    public static TelnyxMediaFrame Parse(string json) =>
        JsonSerializer.Deserialize<TelnyxMediaFrame>(json, Options)
        ?? throw new JsonException("Empty or invalid Telnyx media frame.");

    public static string ComposeMedia(ReadOnlySpan<byte> audio)
    {
        var payload = Convert.ToBase64String(audio);
        return JsonSerializer.Serialize(new
        {
            @event = "media",
            media = new { payload }
        }, Options);
    }

    public static string ComposeClear() => """{"event":"clear"}""";

    public sealed class StartPayload
    {
        [JsonPropertyName("call_control_id")] public string? CallControlId { get; init; }
        [JsonPropertyName("client_state")] public string? ClientState { get; init; }
        [JsonPropertyName("media_format")] public MediaFormat MediaFormat { get; init; } = new();
    }

    public sealed class MediaFormat
    {
        [JsonPropertyName("encoding")] public string Encoding { get; init; } = "";
        [JsonPropertyName("sample_rate")] public int SampleRate { get; init; }
        [JsonPropertyName("channels")] public int Channels { get; init; }
    }

    public sealed class MediaPayload
    {
        [JsonPropertyName("track")] public string Track { get; init; } = "";
        [JsonPropertyName("chunk")] public string? Chunk { get; init; }
        [JsonPropertyName("timestamp")] public string? Timestamp { get; init; }
        [JsonPropertyName("payload")] public string Payload { get; init; } = "";

        [JsonIgnore] public byte[] PayloadBytes => Convert.FromBase64String(Payload);
    }

    public sealed class StopPayload
    {
        [JsonPropertyName("reason")] public string? Reason { get; init; }
    }

    public sealed class DtmfPayload
    {
        [JsonPropertyName("digit")] public string Digit { get; init; } = "";
    }
}
```

- [ ] **Step 4: Run, expect PASS.**

```bash
dotnet test --filter TelnyxMediaFrameTests
```

- [ ] **Step 5: Commit.**

```bash
git add src/agent/OpenAgent.Channel.Telnyx/TelnyxMediaFrame.cs src/agent/OpenAgent.Tests/TelnyxMediaFrameTests.cs
git commit -m "feat(telnyx): media frame parse/compose"
```

---

## Task 12: TelnyxCallControlClient

**Files:**

- Create: `src/agent/OpenAgent.Channel.Telnyx/TelnyxCallControlClient.cs`
- Test: `src/agent/OpenAgent.Tests/TelnyxCallControlClientTests.cs`

- [ ] **Step 1: Write the failing tests.**

```csharp
// OpenAgent.Tests/TelnyxCallControlClientTests.cs
using System.Net;
using System.Net.Http;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using OpenAgent.Channel.Telnyx;
using Xunit;

namespace OpenAgent.Tests;

public class TelnyxCallControlClientTests
{
    [Fact]
    public async Task AnswerAsync_PostsToCorrectUrl_WithBearerAuth()
    {
        var handler = new RecordingHandler(HttpStatusCode.OK, "{}");
        using var http = new HttpClient(handler);
        var client = new TelnyxCallControlClient(http, "API_KEY", NullLogger<TelnyxCallControlClient>.Instance);

        await client.AnswerAsync("call-123", default);

        Assert.Equal(HttpMethod.Post, handler.LastRequest!.Method);
        Assert.Equal("https://api.telnyx.com/v2/calls/call-123/actions/answer", handler.LastRequest.RequestUri!.ToString());
        Assert.Equal("Bearer", handler.LastRequest.Headers.Authorization!.Scheme);
        Assert.Equal("API_KEY", handler.LastRequest.Headers.Authorization.Parameter);
    }

    [Fact]
    public async Task StreamingStartAsync_PostsExpectedBody()
    {
        var handler = new RecordingHandler(HttpStatusCode.OK, "{}");
        using var http = new HttpClient(handler);
        var client = new TelnyxCallControlClient(http, "API_KEY", NullLogger<TelnyxCallControlClient>.Instance);

        await client.StreamingStartAsync("call-123", "wss://us/stream?call=call-123", default);

        Assert.Contains("call-123/actions/streaming_start", handler.LastRequest!.RequestUri!.ToString());
        var body = await handler.LastRequest.Content!.ReadAsStringAsync();
        var doc = JsonDocument.Parse(body);
        Assert.Equal("wss://us/stream?call=call-123", doc.RootElement.GetProperty("stream_url").GetString());
        Assert.Equal("rtp", doc.RootElement.GetProperty("stream_bidirectional_mode").GetString());
        Assert.Equal("PCMU", doc.RootElement.GetProperty("stream_bidirectional_codec").GetString());
        Assert.Equal(8000, doc.RootElement.GetProperty("stream_bidirectional_sampling_rate").GetInt32());
        Assert.Equal("self", doc.RootElement.GetProperty("stream_bidirectional_target_legs").GetString());
        Assert.Equal("inbound_track", doc.RootElement.GetProperty("stream_track").GetString());
        Assert.NotNull(doc.RootElement.GetProperty("client_state").GetString());
    }

    [Fact]
    public async Task HangupAsync_404_TreatedAsSuccess()
    {
        var handler = new RecordingHandler(HttpStatusCode.NotFound, "{\"errors\":[{\"code\":\"10005\"}]}");
        using var http = new HttpClient(handler);
        var client = new TelnyxCallControlClient(http, "API_KEY", NullLogger<TelnyxCallControlClient>.Instance);

        await client.HangupAsync("already-gone", default); // should NOT throw
    }

    [Fact]
    public async Task HangupAsync_500_Throws()
    {
        var handler = new RecordingHandler(HttpStatusCode.InternalServerError, "{}");
        using var http = new HttpClient(handler);
        var client = new TelnyxCallControlClient(http, "API_KEY", NullLogger<TelnyxCallControlClient>.Instance);

        await Assert.ThrowsAsync<HttpRequestException>(() => client.HangupAsync("call-1", default));
    }

    private sealed class RecordingHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            // copy request so consumer can read content
            var copy = new HttpRequestMessage(request.Method, request.RequestUri);
            foreach (var h in request.Headers) copy.Headers.Add(h.Key, h.Value);
            if (request.Content is not null)
            {
                var bytes = await request.Content.ReadAsByteArrayAsync(ct);
                copy.Content = new ByteArrayContent(bytes);
                foreach (var h in request.Content.Headers) copy.Content.Headers.Add(h.Key, h.Value);
            }
            LastRequest = copy;
            return new HttpResponseMessage(status) { Content = new StringContent(body) };
        }
    }
}
```

- [ ] **Step 2: Run, expect FAIL.**

- [ ] **Step 3: Implement `TelnyxCallControlClient`.**

```csharp
// OpenAgent.Channel.Telnyx/TelnyxCallControlClient.cs
using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.Extensions.Logging;

namespace OpenAgent.Channel.Telnyx;

/// <summary>
/// Thin wrapper over the Telnyx Call Control v2 REST API for the three actions the bridge needs:
/// answer, streaming_start, hangup. Hangup is idempotent — 404/410 are treated as success because
/// multiple teardown paths legitimately race against an already-ended call.
/// </summary>
public sealed class TelnyxCallControlClient
{
    private const string ApiBase = "https://api.telnyx.com/v2";

    private readonly HttpClient _http;
    private readonly string _apiKey;
    private readonly ILogger<TelnyxCallControlClient> _logger;

    public TelnyxCallControlClient(HttpClient http, string apiKey, ILogger<TelnyxCallControlClient> logger)
    {
        _http = http;
        _apiKey = apiKey;
        _logger = logger;
    }

    public Task AnswerAsync(string callControlId, CancellationToken ct) =>
        PostAsync($"calls/{callControlId}/actions/answer", new { }, idempotent404: false, ct);

    public Task StreamingStartAsync(string callControlId, string streamUrl, CancellationToken ct)
    {
        var clientState = Convert.ToBase64String(Encoding.UTF8.GetBytes(callControlId));
        var body = new
        {
            stream_url = streamUrl,
            stream_track = "inbound_track",
            stream_bidirectional_mode = "rtp",
            stream_bidirectional_codec = "PCMU",
            stream_bidirectional_sampling_rate = 8000,
            stream_bidirectional_target_legs = "self",
            client_state = clientState
        };
        return PostAsync($"calls/{callControlId}/actions/streaming_start", body, idempotent404: false, ct);
    }

    public Task HangupAsync(string callControlId, CancellationToken ct) =>
        PostAsync($"calls/{callControlId}/actions/hangup", new { }, idempotent404: true, ct);

    private async Task PostAsync<T>(string path, T body, bool idempotent404, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{ApiBase}/{path}");
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _apiKey);
        req.Content = JsonContent.Create(body);

        using var res = await _http.SendAsync(req, ct);
        if (idempotent404 && (res.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone))
        {
            _logger.LogDebug("Telnyx {Path} returned {Status} — treated as already-completed", path, res.StatusCode);
            return;
        }
        res.EnsureSuccessStatusCode();
    }
}
```

- [ ] **Step 4: Run tests, expect PASS.**

```bash
dotnet test --filter TelnyxCallControlClientTests
```

- [ ] **Step 5: Commit.**

```bash
git add src/agent/OpenAgent.Channel.Telnyx/TelnyxCallControlClient.cs src/agent/OpenAgent.Tests/TelnyxCallControlClientTests.cs
git commit -m "feat(telnyx): Call Control REST client with idempotent hangup"
```

---

## Task 13: ThinkingClipFactory (procedural µ-law clip generator)

**Files:**

- Create: `src/agent/OpenAgent.Channel.Telnyx/ThinkingClipFactory.cs`
- Test: `src/agent/OpenAgent.Tests/ThinkingClipFactoryTests.cs`

- [ ] **Step 1: Write the failing tests.**

```csharp
// OpenAgent.Tests/ThinkingClipFactoryTests.cs
using OpenAgent.Channel.Telnyx;
using Xunit;

namespace OpenAgent.Tests;

public class ThinkingClipFactoryTests
{
    [Fact]
    public void Generate_ReturnsExpectedFrameCount_ForTwoSeconds()
    {
        var clip = ThinkingClipFactory.Generate();
        // 2 seconds @ 8 kHz = 16000 samples = 16000 µ-law bytes (one byte per sample)
        Assert.Equal(16000, clip.Length);
    }

    [Fact]
    public void Generate_LoopBoundary_HasCosineFade_NoClicks()
    {
        var clip = ThinkingClipFactory.Generate();
        // Last 50 ms (400 samples) and first 50 ms should both be near silence (µ-law silence = 0xFF or 0x7F).
        // We assert the absolute amplitude near both edges is below the clip-mean amplitude — proxy for fade.
        var mean = MeanAmplitude(clip, 400, clip.Length - 400);
        var headEdge = MeanAmplitude(clip, 0, 50);
        var tailEdge = MeanAmplitude(clip, clip.Length - 50, clip.Length);
        Assert.True(headEdge < mean * 0.75, $"head edge {headEdge} should be quieter than mean {mean}");
        Assert.True(tailEdge < mean * 0.75, $"tail edge {tailEdge} should be quieter than mean {mean}");
    }

    private static double MeanAmplitude(byte[] ulaw, int start, int end)
    {
        // µ-law decode is not necessary for a relative comparison; bytes near 0xFF are silence (negative)
        // and bytes near 0x7F are silence (positive). We measure |b - 0x7F| as a coarse proxy.
        double sum = 0;
        for (var i = start; i < end; i++) sum += Math.Abs(ulaw[i] - 0x7F);
        return sum / (end - start);
    }
}
```

- [ ] **Step 2: Run, expect FAIL.**

- [ ] **Step 3: Implement `ThinkingClipFactory`.**

```csharp
// OpenAgent.Channel.Telnyx/ThinkingClipFactory.cs
namespace OpenAgent.Channel.Telnyx;

/// <summary>
/// Generates the default seamless-loop thinking clip used by <see cref="TelnyxMediaBridge"/>'s
/// thinking pump. The clip is band-limited soft pink noise (300-1000 Hz proxy via summed sines)
/// encoded as 8 kHz µ-law mono with a ~50 ms cosine fade across the loop boundary so repeats
/// are click-free. No third-party audio asset, no licensing concern.
/// </summary>
public static class ThinkingClipFactory
{
    private const int SampleRate = 8000;
    private const double Duration = 2.0;
    private const double FadeSeconds = 0.05;

    public static byte[] Generate()
    {
        var sampleCount = (int)(SampleRate * Duration);
        var fadeSamples = (int)(SampleRate * FadeSeconds);
        var pcm = new short[sampleCount];

        // A few overlapping low-frequency sines simulate soft ambient noise without true RNG —
        // deterministic output makes tests reproducible.
        var freqs = new[] { 320.0, 470.0, 610.0, 880.0 };
        var phases = new[] { 0.0, 0.7, 1.4, 2.1 };
        const double amp = 1500; // ~ -28 dBFS

        for (var i = 0; i < sampleCount; i++)
        {
            var t = (double)i / SampleRate;
            double s = 0;
            for (var k = 0; k < freqs.Length; k++)
                s += Math.Sin(2 * Math.PI * freqs[k] * t + phases[k]);
            pcm[i] = (short)(s * amp / freqs.Length);
        }

        // Cosine fade-in at the head AND fade-out at the tail. Because head fades up from 0 and
        // tail fades down to 0, the loop boundary (tail->head) is silent on both sides — click-free.
        for (var i = 0; i < fadeSamples; i++)
        {
            var w = (1 - Math.Cos(Math.PI * i / fadeSamples)) / 2;
            pcm[i] = (short)(pcm[i] * w);
            pcm[sampleCount - 1 - i] = (short)(pcm[sampleCount - 1 - i] * w);
        }

        var ulaw = new byte[sampleCount];
        for (var i = 0; i < sampleCount; i++) ulaw[i] = LinearToUlaw(pcm[i]);
        return ulaw;
    }

    // Standard ITU-T G.711 µ-law encoding.
    private static byte LinearToUlaw(short pcm)
    {
        const int BIAS = 0x84;
        const int CLIP = 32635;
        var sign = (pcm >> 8) & 0x80;
        if (sign != 0) pcm = (short)-pcm;
        if (pcm > CLIP) pcm = CLIP;
        pcm += BIAS;
        var exponent = 7;
        for (var mask = 0x4000; (pcm & mask) == 0 && exponent > 0; mask >>= 1) exponent--;
        var mantissa = (pcm >> (exponent + 3)) & 0x0F;
        var ulaw = ~(sign | (exponent << 4) | mantissa) & 0xFF;
        return (byte)ulaw;
    }
}
```

- [ ] **Step 4: Run tests, expect PASS.**

- [ ] **Step 5: Commit.**

```bash
git add src/agent/OpenAgent.Channel.Telnyx/ThinkingClipFactory.cs src/agent/OpenAgent.Tests/ThinkingClipFactoryTests.cs
git commit -m "feat(telnyx): procedural thinking clip generator with cosine-fade loop seam"
```

---

## Task 14: TelnyxBridgeRegistry (lookup of active bridges by conversation id)

`EndCallTool` needs to find the active bridge for the call's conversation. A small singleton registry decouples the tool from the per-connection provider.

**Files:**

- Create: `src/agent/OpenAgent.Channel.Telnyx/TelnyxBridgeRegistry.cs`
- Test: `src/agent/OpenAgent.Tests/TelnyxBridgeRegistryTests.cs`

- [ ] **Step 1: Write the failing tests.**

```csharp
// OpenAgent.Tests/TelnyxBridgeRegistryTests.cs
using OpenAgent.Channel.Telnyx;
using Xunit;

namespace OpenAgent.Tests;

public class TelnyxBridgeRegistryTests
{
    [Fact]
    public void Register_Then_TryGet_Returns_The_Bridge()
    {
        var reg = new TelnyxBridgeRegistry();
        var fake = new object();
        reg.Register("conv-1", fake);
        Assert.True(reg.TryGet("conv-1", out var got));
        Assert.Same(fake, got);
    }

    [Fact]
    public void Unregister_Removes()
    {
        var reg = new TelnyxBridgeRegistry();
        var fake = new object();
        reg.Register("conv-1", fake);
        reg.Unregister("conv-1");
        Assert.False(reg.TryGet("conv-1", out _));
    }

    [Fact]
    public void TryGet_Unknown_ReturnsFalse()
    {
        var reg = new TelnyxBridgeRegistry();
        Assert.False(reg.TryGet("missing", out _));
    }
}
```

(We use `object` rather than the bridge type so the registry is dependency-free; the actual bridge type is added in Task 18 onwards. Cast at the call site.)

- [ ] **Step 2: Run, expect FAIL.**

- [ ] **Step 3: Implement.**

```csharp
// OpenAgent.Channel.Telnyx/TelnyxBridgeRegistry.cs
using System.Collections.Concurrent;

namespace OpenAgent.Channel.Telnyx;

/// <summary>
/// Per-application registry of active media bridges, keyed by conversation id. Allows
/// <see cref="EndCallTool"/> to find the bridge for a given conversation without going through
/// the channel provider directly. Bridges register themselves at start of <c>RunAsync</c> and
/// unregister in the finally block.
/// </summary>
public sealed class TelnyxBridgeRegistry
{
    private readonly ConcurrentDictionary<string, object> _bridges = new();

    public void Register(string conversationId, object bridge) => _bridges[conversationId] = bridge;
    public void Unregister(string conversationId) => _bridges.TryRemove(conversationId, out _);
    public bool TryGet(string conversationId, out object? bridge) => _bridges.TryGetValue(conversationId, out bridge);
}
```

- [ ] **Step 4: Run tests, expect PASS.**

- [ ] **Step 5: Commit.**

```bash
git add src/agent/OpenAgent.Channel.Telnyx/TelnyxBridgeRegistry.cs src/agent/OpenAgent.Tests/TelnyxBridgeRegistryTests.cs
git commit -m "feat(telnyx): bridge registry for active-call lookup"
```

> **PR2 boundary.** Tasks 8–14 give us a compiling, unit-tested Telnyx project with no integration. Open a PR or merge before starting PR3.

---

## Task 15: TelnyxChannelProviderFactory + TelnyxChannelProvider (lifecycle skeleton)

**Files:**

- Create: `src/agent/OpenAgent.Channel.Telnyx/TelnyxChannelProviderFactory.cs`
- Create: `src/agent/OpenAgent.Channel.Telnyx/TelnyxChannelProvider.cs`
- Test: `src/agent/OpenAgent.Tests/TelnyxChannelProviderFactoryTests.cs`

- [ ] **Step 1: Write the failing factory tests.**

```csharp
// OpenAgent.Tests/TelnyxChannelProviderFactoryTests.cs
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using OpenAgent.Channel.Telnyx;
using OpenAgent.Models.Connections;
using Xunit;

namespace OpenAgent.Tests;

public class TelnyxChannelProviderFactoryTests
{
    [Fact]
    public void Type_IsTelnyx()
    {
        var factory = NewFactory();
        Assert.Equal("telnyx", factory.Type);
    }

    [Fact]
    public void ConfigFields_IncludesAllRequired()
    {
        var factory = NewFactory();
        var keys = factory.ConfigFields.Select(f => f.Key).ToList();
        Assert.Contains("apiKey", keys);
        Assert.Contains("phoneNumber", keys);
        Assert.Contains("baseUrl", keys);
        Assert.Contains("callControlAppId", keys);
        Assert.Contains("webhookPublicKey", keys);
        Assert.Contains("allowedNumbers", keys);
    }

    [Fact]
    public void Create_DeserializesOptions()
    {
        var json = """
        {"apiKey":"K","phoneNumber":"+45","baseUrl":"https://x","callControlAppId":"app","webhookPublicKey":"PEM","allowedNumbers":"+4520,+4530","webhookId":"abc"}
        """;
        var conn = new Connection
        {
            Id = "c", Name = "n", Type = "telnyx", Enabled = true,
            Config = JsonSerializer.Deserialize<JsonElement>(json)
        };
        var factory = NewFactory();
        var provider = (TelnyxChannelProvider)factory.Create(conn);
        Assert.Equal("K", provider.Options.ApiKey);
        Assert.Equal("+45", provider.Options.PhoneNumber);
        Assert.Equal(["+4520","+4530"], provider.Options.AllowedNumbers);
        Assert.Equal("abc", provider.Options.WebhookId);
    }

    private static TelnyxChannelProviderFactory NewFactory() =>
        new TelnyxChannelProviderFactory(
            store: null!,
            connectionStore: null!,
            voiceProviderResolver: _ => null!,
            agentConfig: null!,
            bridgeRegistry: new TelnyxBridgeRegistry(),
            httpClientFactory: null!,
            loggerFactory: NullLoggerFactory.Instance);
}
```

- [ ] **Step 2: Run, expect FAIL.**

- [ ] **Step 3: Implement `TelnyxChannelProviderFactory.cs`.**

```csharp
using System.Text.Json;
using Microsoft.Extensions.Logging;
using OpenAgent.Contracts;
using OpenAgent.Models.Configs;
using OpenAgent.Models.Connections;
using OpenAgent.Models.Providers;

namespace OpenAgent.Channel.Telnyx;

public sealed class TelnyxChannelProviderFactory : IChannelProviderFactory
{
    private readonly IConversationStore _store;
    private readonly IConnectionStore _connectionStore;
    private readonly Func<string, ILlmVoiceProvider> _voiceProviderResolver;
    private readonly AgentConfig _agentConfig;
    private readonly TelnyxBridgeRegistry _bridgeRegistry;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILoggerFactory _loggerFactory;

    public string Type => "telnyx";
    public string DisplayName => "Telnyx";

    public IReadOnlyList<ProviderConfigField> ConfigFields { get; } =
    [
        new() { Key = "apiKey",           Label = "Telnyx API Key",                                  Type = "Secret", Required = true },
        new() { Key = "phoneNumber",      Label = "Phone Number (E.164)",                            Type = "String", Required = true },
        new() { Key = "baseUrl",          Label = "Public Base URL (https)",                         Type = "String", Required = true },
        new() { Key = "callControlAppId", Label = "Call Control Connection ID",                      Type = "String", Required = true },
        new() { Key = "webhookPublicKey", Label = "Webhook Public Key (PEM, leave empty for dev)",   Type = "Secret" },
        new() { Key = "allowedNumbers",   Label = "Allowed Caller Numbers (comma-separated, empty = allow all)", Type = "String" },
        new() { Key = "thinkingClipPath", Label = "Custom Thinking Clip Path (relative to dataPath, optional)", Type = "String" },
    ];

    public ChannelSetupStep? SetupStep => null;

    public TelnyxChannelProviderFactory(
        IConversationStore store,
        IConnectionStore connectionStore,
        Func<string, ILlmVoiceProvider> voiceProviderResolver,
        AgentConfig agentConfig,
        TelnyxBridgeRegistry bridgeRegistry,
        IHttpClientFactory httpClientFactory,
        ILoggerFactory loggerFactory)
    {
        _store = store;
        _connectionStore = connectionStore;
        _voiceProviderResolver = voiceProviderResolver;
        _agentConfig = agentConfig;
        _bridgeRegistry = bridgeRegistry;
        _httpClientFactory = httpClientFactory;
        _loggerFactory = loggerFactory;
    }

    public IChannelProvider Create(Connection connection)
    {
        var options = ParseOptions(connection.Config);
        return new TelnyxChannelProvider(
            options,
            connection.Id,
            _store,
            _connectionStore,
            _voiceProviderResolver,
            _agentConfig,
            _bridgeRegistry,
            _httpClientFactory,
            _loggerFactory);
    }

    private static TelnyxOptions ParseOptions(JsonElement config)
    {
        var opts = new TelnyxOptions();
        if (config.ValueKind != JsonValueKind.Object) return opts;
        if (config.TryGetProperty("apiKey", out var p) && p.ValueKind == JsonValueKind.String) opts.ApiKey = p.GetString();
        if (config.TryGetProperty("phoneNumber", out p) && p.ValueKind == JsonValueKind.String) opts.PhoneNumber = p.GetString();
        if (config.TryGetProperty("baseUrl", out p) && p.ValueKind == JsonValueKind.String) opts.BaseUrl = p.GetString();
        if (config.TryGetProperty("callControlAppId", out p) && p.ValueKind == JsonValueKind.String) opts.CallControlAppId = p.GetString();
        if (config.TryGetProperty("webhookPublicKey", out p) && p.ValueKind == JsonValueKind.String) opts.WebhookPublicKey = p.GetString();
        if (config.TryGetProperty("thinkingClipPath", out p) && p.ValueKind == JsonValueKind.String) opts.ThinkingClipPath = p.GetString();
        if (config.TryGetProperty("webhookId", out p) && p.ValueKind == JsonValueKind.String) opts.WebhookId = p.GetString();
        if (config.TryGetProperty("allowedNumbers", out p))
        {
            opts.AllowedNumbers = p.ValueKind switch
            {
                JsonValueKind.String when p.GetString() is { Length: > 0 } s =>
                    s.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList(),
                JsonValueKind.Array => p.EnumerateArray()
                    .Where(e => e.ValueKind == JsonValueKind.String)
                    .Select(e => e.GetString()!)
                    .Where(s => s.Length > 0)
                    .ToList(),
                _ => [],
            };
        }
        return opts;
    }
}
```

- [ ] **Step 4: Implement `TelnyxChannelProvider.cs`.**

```csharp
using System.Text.Json;
using Microsoft.Extensions.Logging;
using OpenAgent.Contracts;
using OpenAgent.Models.Configs;

namespace OpenAgent.Channel.Telnyx;

/// <summary>
/// Owns the runtime state for one Telnyx connection: the Call Control client, the signature
/// verifier, the allow-list, the procedural thinking clip, and the pending-bridge dictionary.
/// Active bridges are tracked in the global <see cref="TelnyxBridgeRegistry"/> instead so the
/// EndCallTool (an app-singleton) can find them without going through this provider.
/// </summary>
public sealed class TelnyxChannelProvider : IChannelProvider
{
    private readonly TelnyxOptions _options;
    private readonly string _connectionId;
    private readonly IConnectionStore _connectionStore;
    private readonly IConversationStore _store;
    private readonly Func<string, ILlmVoiceProvider> _voiceProviderResolver;
    private readonly AgentConfig _agentConfig;
    private readonly TelnyxBridgeRegistry _bridgeRegistry;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<TelnyxChannelProvider> _logger;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, PendingBridge> _pending = new();

    public TelnyxOptions Options => _options;
    public string ConnectionId => _connectionId;
    public TelnyxBridgeRegistry BridgeRegistry => _bridgeRegistry;
    public TelnyxSignatureVerifier SignatureVerifier { get; }
    public TelnyxCallControlClient CallControlClient { get; }
    public byte[] ThinkingClip { get; private set; } = [];
    public AgentConfig AgentConfig => _agentConfig;
    public IConversationStore ConversationStore => _store;
    public Func<string, ILlmVoiceProvider> VoiceProviderResolver => _voiceProviderResolver;
    public ILoggerFactory LoggerFactory => _loggerFactory;

    public TelnyxChannelProvider(
        TelnyxOptions options,
        string connectionId,
        IConversationStore store,
        IConnectionStore connectionStore,
        Func<string, ILlmVoiceProvider> voiceProviderResolver,
        AgentConfig agentConfig,
        TelnyxBridgeRegistry bridgeRegistry,
        IHttpClientFactory httpClientFactory,
        ILoggerFactory loggerFactory)
    {
        _options = options;
        _connectionId = connectionId;
        _store = store;
        _connectionStore = connectionStore;
        _voiceProviderResolver = voiceProviderResolver;
        _agentConfig = agentConfig;
        _bridgeRegistry = bridgeRegistry;
        _httpClientFactory = httpClientFactory;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<TelnyxChannelProvider>();

        SignatureVerifier = new TelnyxSignatureVerifier(loggerFactory.CreateLogger<TelnyxSignatureVerifier>());
        CallControlClient = new TelnyxCallControlClient(
            httpClientFactory.CreateClient(nameof(TelnyxCallControlClient)),
            options.ApiKey ?? throw new InvalidOperationException("Telnyx ApiKey is required."),
            loggerFactory.CreateLogger<TelnyxCallControlClient>());
    }

    public Task StartAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_options.BaseUrl))
            throw new InvalidOperationException("Telnyx BaseUrl is required.");
        if (string.IsNullOrWhiteSpace(_options.CallControlAppId))
            throw new InvalidOperationException("Telnyx CallControlAppId is required.");

        if (string.IsNullOrWhiteSpace(_options.WebhookId))
        {
            _options.WebhookId = Guid.NewGuid().ToString("N")[..12];
            var connection = _connectionStore.Load(_connectionId);
            if (connection is not null)
            {
                var dict = JsonSerializer.Deserialize<Dictionary<string, object>>(connection.Config) ?? [];
                dict["webhookId"] = _options.WebhookId;
                connection.Config = JsonSerializer.SerializeToElement(dict);
                _connectionStore.Save(connection);
                _logger.LogInformation("Telnyx: generated webhookId {WebhookId} for connection {ConnectionId}",
                    _options.WebhookId, _connectionId);
            }
        }

        ThinkingClip = LoadThinkingClip();

        _logger.LogInformation(
            "Telnyx [{ConnectionId}] started: phone={Phone}, webhookId={WebhookId}, allow={Allow}",
            _connectionId, _options.PhoneNumber, _options.WebhookId, _options.AllowedNumbers.Count);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;

    public bool TryRegisterPending(string callControlId, PendingBridge pending) =>
        _pending.TryAdd(callControlId, pending);

    public bool TryDequeuePending(string callControlId, out PendingBridge? pending)
    {
        var ok = _pending.TryRemove(callControlId, out var p);
        pending = p;
        return ok;
    }

    private byte[] LoadThinkingClip()
    {
        if (string.IsNullOrWhiteSpace(_options.ThinkingClipPath))
            return ThinkingClipFactory.Generate();

        var fullPath = Path.Combine(_agentConfig.DataPath, _options.ThinkingClipPath);
        if (!File.Exists(fullPath))
        {
            _logger.LogWarning("Telnyx ThinkingClipPath {Path} missing — falling back to procedural default", fullPath);
            return ThinkingClipFactory.Generate();
        }

        var bytes = File.ReadAllBytes(fullPath);
        // µ-law 8 kHz, 20 ms = 160 bytes per frame; require multiple of frame size for clean looping.
        if (bytes.Length % 160 != 0)
        {
            _logger.LogWarning("Telnyx ThinkingClipPath {Path} is not a multiple of 160 bytes — falling back to procedural default", fullPath);
            return ThinkingClipFactory.Generate();
        }
        return bytes;
    }
}

public sealed record PendingBridge(
    string CallControlId,
    string ConversationId,
    string VoiceProviderKey,
    CancellationTokenSource Cts);
```

- [ ] **Step 5: Run, expect PASS.**

- [ ] **Step 6: Commit.**

```bash
git add src/agent/OpenAgent.Channel.Telnyx/TelnyxChannelProviderFactory.cs \
        src/agent/OpenAgent.Channel.Telnyx/TelnyxChannelProvider.cs \
        src/agent/OpenAgent.Tests/TelnyxChannelProviderFactoryTests.cs
git commit -m "feat(telnyx): channel provider + factory with config parsing"
```

---

## Task 16: TelnyxWebhookEndpoints (HTTP — call.initiated, call.hangup, streaming.*)

**Files:**

- Create: `src/agent/OpenAgent.Channel.Telnyx/TelnyxWebhookEndpoints.cs`
- Test: `src/agent/OpenAgent.Tests/TelnyxWebhookEndpointTests.cs`

- [ ] **Step 1: Write the failing tests.**

```csharp
// OpenAgent.Tests/TelnyxWebhookEndpointTests.cs
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using OpenAgent;
using Xunit;

namespace OpenAgent.Tests;

public class TelnyxWebhookEndpointTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public TelnyxWebhookEndpointTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task UnknownWebhookId_Returns404()
    {
        using var client = _factory.CreateClient();
        var body = JsonSerializer.Serialize(new { data = new { event_type = "call.initiated" } });
        var res = await client.PostAsync("/api/webhook/telnyx/unknown1234/call",
            new StringContent(body, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }

    [Fact]
    public async Task CallInitiated_BadConnectionId_Returns401()
    {
        // requires test factory to register a Telnyx connection with known webhookId/connectionId/etc.
        // Use TestSetup.SeedTelnyxConnection helper (added in this task).
        using var (factory, webhookId, _) = TestSetup.WithTelnyxConnection(_factory);
        using var client = factory.CreateClient();

        var body = JsonSerializer.Serialize(new
        {
            data = new
            {
                event_type = "call.initiated",
                payload = new { call_control_id = "call-1", from = "+4520", to = "+4535150636", connection_id = "WRONG" }
            }
        });
        var res = await client.PostAsync($"/api/webhook/telnyx/{webhookId}/call",
            new StringContent(body, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    // Additional cases (allowlist, signature failure with key set) follow the same shape.
}
```

The test depends on a `TestSetup.WithTelnyxConnection` helper that creates a `WebApplicationFactory` with a known Telnyx connection seeded into the connection store and a fake `TelnyxCallControlClient`. Add the helper in this task. (See the existing `TestSetup` class in `OpenAgent.Tests/TestSetup.cs` for the pattern.)

- [ ] **Step 2: Run, expect FAIL.**

- [ ] **Step 3: Implement `TelnyxWebhookEndpoints.cs`.**

```csharp
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using OpenAgent.Contracts;
using OpenAgent.Models.Conversations;

namespace OpenAgent.Channel.Telnyx;

public static class TelnyxWebhookEndpoints
{
    public static WebApplication MapTelnyxWebhookEndpoints(this WebApplication app)
    {
        app.MapPost("/api/webhook/telnyx/{webhookId}/call", HandleCallEvent)
           .AllowAnonymous()
           .WithName("TelnyxCallWebhook");
        return app;
    }

    private static async Task<IResult> HandleCallEvent(
        string webhookId,
        HttpRequest request,
        IConnectionManager connectionManager,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var logger = loggerFactory.CreateLogger("TelnyxWebhook");

        // Find the running provider by webhookId
        var provider = connectionManager.GetProviders()
            .Select(p => p.Provider)
            .OfType<TelnyxChannelProvider>()
            .FirstOrDefault(p => p.Options.WebhookId == webhookId);
        if (provider is null) return Results.NotFound();

        // Buffer body so we can both verify signature and parse JSON
        using var ms = new MemoryStream();
        await request.Body.CopyToAsync(ms, ct);
        var rawBody = ms.ToArray();

        // Verify ED25519 signature
        var sig = request.Headers["Telnyx-Signature-ed25519"].ToString();
        var ts  = request.Headers["Telnyx-Timestamp"].ToString();
        if (!provider.SignatureVerifier.Verify(provider.Options.WebhookPublicKey, sig, ts, rawBody, DateTimeOffset.UtcNow))
        {
            logger.LogWarning("Telnyx webhook signature failed for {ConnectionId}", provider.ConnectionId);
            return Results.Unauthorized();
        }

        // Parse the envelope
        TelnyxWebhookEnvelope env;
        try { env = JsonSerializer.Deserialize<TelnyxWebhookEnvelope>(rawBody)
              ?? throw new JsonException("null"); }
        catch (Exception ex) { logger.LogWarning(ex, "Telnyx webhook JSON malformed"); return Results.BadRequest(); }

        // Validate connection_id matches our configured Call Control connection
        if (!string.Equals(env.Data?.Payload?.ConnectionId, provider.Options.CallControlAppId, StringComparison.Ordinal))
        {
            logger.LogWarning("Telnyx webhook connection_id mismatch (got {Got}, expected {Want})",
                env.Data?.Payload?.ConnectionId, provider.Options.CallControlAppId);
            return Results.Unauthorized();
        }

        return env.Data?.EventType switch
        {
            "call.initiated"     => await OnCallInitiated(provider, env, loggerFactory, ct),
            "call.hangup"        => await OnCallHangup(provider, env, ct),
            "streaming.started"  => Results.Ok(),
            "streaming.stopped"  => Results.Ok(),
            "streaming.failed"   => await OnStreamingFailed(provider, env, ct),
            _ => Results.Ok(),
        };
    }

    private static async Task<IResult> OnCallInitiated(
        TelnyxChannelProvider provider,
        TelnyxWebhookEnvelope env,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var p = env.Data!.Payload!;
        var from = p.From ?? "";
        var callControlId = p.CallControlId ?? "";

        // Allowlist
        if (provider.Options.AllowedNumbers.Count > 0 && !provider.Options.AllowedNumbers.Contains(from))
        {
            await provider.CallControlClient.HangupAsync(callControlId, ct);
            return Results.Ok();
        }

        // Conversation lookup
        var conv = provider.ConversationStore.FindOrCreateChannelConversation(
            channelType: "telnyx",
            connectionId: provider.ConnectionId,
            channelChatId: from,
            source: "telnyx",
            type: ConversationType.Phone,
            provider: provider.AgentConfig.VoiceProvider,
            model: provider.AgentConfig.VoiceModel);

        if (!string.Equals(conv.DisplayName, from, StringComparison.Ordinal))
            provider.ConversationStore.UpdateDisplayName(conv.Id, from);

        // Answer the call
        await provider.CallControlClient.AnswerAsync(callControlId, ct);

        // Register pending bridge BEFORE issuing streaming_start
        var cts = new CancellationTokenSource();
        var pending = new PendingBridge(callControlId, conv.Id, conv.Provider, cts);
        if (!provider.TryRegisterPending(callControlId, pending))
            return Results.Ok(); // duplicate event, ignore

        // Self-evict + hang up if WS doesn't connect within 30 s
        var token = cts.Token;
        cts.CancelAfter(TimeSpan.FromSeconds(30));
        token.Register(async () =>
        {
            if (provider.TryDequeuePending(callControlId, out _))
            {
                try { await provider.CallControlClient.HangupAsync(callControlId, default); }
                catch { /* idempotent */ }
            }
        });

        // Start streaming
        var wsUrl = BuildStreamUrl(provider, callControlId);
        try
        {
            await provider.CallControlClient.StreamingStartAsync(callControlId, wsUrl, ct);
        }
        catch (Exception ex)
        {
            loggerFactory.CreateLogger("TelnyxWebhook").LogWarning(ex, "streaming_start failed; rolling back call");
            provider.TryDequeuePending(callControlId, out _);
            await provider.CallControlClient.HangupAsync(callControlId, default);
            return Results.Ok();
        }
        return Results.Ok();
    }

    private static async Task<IResult> OnCallHangup(TelnyxChannelProvider provider, TelnyxWebhookEnvelope env, CancellationToken ct)
    {
        var callControlId = env.Data!.Payload!.CallControlId ?? "";
        // Pending entry cleanup
        provider.TryDequeuePending(callControlId, out _);
        // Active bridge cleanup happens in the bridge itself via WS close — nothing to do here
        await Task.CompletedTask;
        return Results.Ok();
    }

    private static async Task<IResult> OnStreamingFailed(TelnyxChannelProvider provider, TelnyxWebhookEnvelope env, CancellationToken ct)
    {
        var callControlId = env.Data!.Payload!.CallControlId ?? "";
        provider.TryDequeuePending(callControlId, out _);
        await provider.CallControlClient.HangupAsync(callControlId, default);
        return Results.Ok();
    }

    private static string BuildStreamUrl(TelnyxChannelProvider provider, string callControlId)
    {
        var baseUrl = provider.Options.BaseUrl!.TrimEnd('/');
        var wssBase = baseUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            ? "wss://" + baseUrl["https://".Length..]
            : "ws://" + baseUrl["http://".Length..];
        return $"{wssBase}/api/webhook/telnyx/{provider.Options.WebhookId}/stream?call={Uri.EscapeDataString(callControlId)}";
    }

    private sealed class TelnyxWebhookEnvelope
    {
        public TelnyxData? Data { get; set; }
    }
    private sealed class TelnyxData
    {
        public string? EventType { get; set; }
        public TelnyxPayload? Payload { get; set; }
    }
    private sealed class TelnyxPayload
    {
        public string? CallControlId { get; set; }
        public string? ConnectionId { get; set; }
        public string? From { get; set; }
        public string? To { get; set; }
    }
}
```

> Note: the JSON envelope class names use PascalCase but System.Text.Json default snake_case binding requires explicit converter or attributes. Use `JsonSerializerOptions { PropertyNameCaseInsensitive = true, PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower }` consistently in the deserialise call (see `TelnyxMediaFrame.Options` for the pattern).

- [ ] **Step 4: Run tests.**

```bash
dotnet test --filter TelnyxWebhookEndpointTests
```

- [ ] **Step 5: Commit.**

```bash
git add src/agent/OpenAgent.Channel.Telnyx/TelnyxWebhookEndpoints.cs src/agent/OpenAgent.Tests/TelnyxWebhookEndpointTests.cs src/agent/OpenAgent.Tests/TestSetup.cs
git commit -m "feat(telnyx): call lifecycle webhook endpoint with rollback + idempotent hangup"
```

---

## Task 17: TelnyxStreamingEndpoint (WebSocket route)

**Files:**

- Create: `src/agent/OpenAgent.Channel.Telnyx/TelnyxStreamingEndpoint.cs`
- Test: `src/agent/OpenAgent.Tests/TelnyxStreamingEndpointTests.cs` (deferred to Task 21 once bridge exists; for now just the route + 404 path)

- [ ] **Step 1: Write a thin failing test for the 404 path.**

```csharp
// OpenAgent.Tests/TelnyxStreamingEndpointTests.cs
using System.Net;
using System.Net.WebSockets;
using Microsoft.AspNetCore.Mvc.Testing;
using OpenAgent;
using Xunit;

namespace OpenAgent.Tests;

public class TelnyxStreamingEndpointTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;
    public TelnyxStreamingEndpointTests(WebApplicationFactory<Program> f) => _factory = f;

    [Fact]
    public async Task UnknownCallControlId_ClosesImmediately()
    {
        var server = _factory.Server;
        var ws = await server.CreateWebSocketClient().ConnectAsync(
            new Uri("ws://localhost/api/webhook/telnyx/abcdef123456/stream?call=unknown"),
            default);
        var buf = new ArraySegment<byte>(new byte[1]);
        var result = await ws.ReceiveAsync(buf, default);
        Assert.Equal(WebSocketMessageType.Close, result.MessageType);
    }
}
```

- [ ] **Step 2: Run, expect FAIL (route doesn't exist).**

- [ ] **Step 3: Implement the endpoint, deferring bridge logic to a `TelnyxMediaBridge.RunAsync` stub.**

```csharp
// OpenAgent.Channel.Telnyx/TelnyxStreamingEndpoint.cs
using System.Net.WebSockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using OpenAgent.Contracts;

namespace OpenAgent.Channel.Telnyx;

public static class TelnyxStreamingEndpoint
{
    public static WebApplication MapTelnyxStreamingEndpoint(this WebApplication app)
    {
        app.Map("/api/webhook/telnyx/{webhookId}/stream", async (
            string webhookId,
            HttpContext context,
            IConnectionManager connectionManager,
            ILoggerFactory loggerFactory,
            CancellationToken ct) =>
        {
            if (!context.WebSockets.IsWebSocketRequest)
            {
                context.Response.StatusCode = 400;
                return;
            }

            var provider = connectionManager.GetProviders()
                .Select(p => p.Provider)
                .OfType<TelnyxChannelProvider>()
                .FirstOrDefault(p => p.Options.WebhookId == webhookId);
            if (provider is null) { context.Response.StatusCode = 404; return; }

            var callControlId = context.Request.Query["call"].ToString();
            if (string.IsNullOrWhiteSpace(callControlId) || !provider.TryDequeuePending(callControlId, out var pending) || pending is null)
            {
                var ws404 = await context.WebSockets.AcceptWebSocketAsync();
                await ws404.CloseAsync(WebSocketCloseStatus.NormalClosure, "unknown call", CancellationToken.None);
                return;
            }

            var ws = await context.WebSockets.AcceptWebSocketAsync();
            using var bridge = new TelnyxMediaBridge(
                provider, pending, ws,
                loggerFactory.CreateLogger<TelnyxMediaBridge>(),
                ct);
            await bridge.RunAsync();
        }).AllowAnonymous();

        return app;
    }
}
```

- [ ] **Step 4: Add a stub `TelnyxMediaBridge` so the project compiles.**

```csharp
// OpenAgent.Channel.Telnyx/TelnyxMediaBridge.cs
using System.Net.WebSockets;
using Microsoft.Extensions.Logging;

namespace OpenAgent.Channel.Telnyx;

public sealed class TelnyxMediaBridge : IDisposable
{
    private readonly TelnyxChannelProvider _provider;
    private readonly PendingBridge _pending;
    private readonly WebSocket _ws;
    private readonly ILogger<TelnyxMediaBridge> _logger;
    private readonly CancellationToken _ct;

    public TelnyxMediaBridge(TelnyxChannelProvider provider, PendingBridge pending, WebSocket ws,
        ILogger<TelnyxMediaBridge> logger, CancellationToken ct)
    {
        _provider = provider;
        _pending = pending;
        _ws = ws;
        _logger = logger;
        _ct = ct;
    }

    public async Task RunAsync()
    {
        // Stubbed in Task 17 — Tasks 18-22 fill this in.
        await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "stub", CancellationToken.None);
    }

    public void Dispose() { }
}
```

- [ ] **Step 5: Run, expect PASS.**

- [ ] **Step 6: Commit.**

```bash
git add src/agent/OpenAgent.Channel.Telnyx/TelnyxStreamingEndpoint.cs \
        src/agent/OpenAgent.Channel.Telnyx/TelnyxMediaBridge.cs \
        src/agent/OpenAgent.Tests/TelnyxStreamingEndpointTests.cs
git commit -m "feat(telnyx): WS streaming endpoint with stub bridge"
```

---

## Task 18: TelnyxMediaBridge — read loop + write loop (audio passthrough only)

**Files:**

- Modify: `src/agent/OpenAgent.Channel.Telnyx/TelnyxMediaBridge.cs`
- Create: `src/agent/OpenAgent.Tests/Fakes/FakeVoiceSession.cs`
- Test: `src/agent/OpenAgent.Tests/TelnyxMediaBridgeReadLoopTests.cs`
- Test: `src/agent/OpenAgent.Tests/TelnyxMediaBridgeWriteLoopTests.cs`

- [ ] **Step 1: Add `FakeVoiceSession`.**

```csharp
// OpenAgent.Tests/Fakes/FakeVoiceSession.cs
using System.Threading.Channels;
using OpenAgent.Contracts;
using OpenAgent.Models.Voice;

namespace OpenAgent.Tests.Fakes;

public sealed class FakeVoiceSession : IVoiceSession
{
    public string SessionId => "fake-session";
    public List<byte[]> SentAudio { get; } = new();
    public bool CommitCalled;
    public bool CancelCalled;
    private readonly Channel<VoiceEvent> _events = Channel.CreateUnbounded<VoiceEvent>();

    public async Task SendAudioAsync(ReadOnlyMemory<byte> audio, CancellationToken ct = default)
    {
        SentAudio.Add(audio.ToArray());
        await Task.CompletedTask;
    }
    public Task CommitAudioAsync(CancellationToken ct = default) { CommitCalled = true; return Task.CompletedTask; }
    public Task CancelResponseAsync(CancellationToken ct = default) { CancelCalled = true; return Task.CompletedTask; }
    public IAsyncEnumerable<VoiceEvent> ReceiveEventsAsync(CancellationToken ct = default) => _events.Reader.ReadAllAsync(ct);
    public ValueTask DisposeAsync() { _events.Writer.Complete(); return ValueTask.CompletedTask; }

    public void Emit(VoiceEvent evt) => _events.Writer.TryWrite(evt);
    public void EndSession() => _events.Writer.TryComplete();
}
```

- [ ] **Step 2: Write the read-loop test.**

```csharp
// OpenAgent.Tests/TelnyxMediaBridgeReadLoopTests.cs
public class TelnyxMediaBridgeReadLoopTests
{
    [Fact]
    public async Task InboundMediaFrame_DecodesPayload_ToSession()
    {
        // Setup: a fake WebSocket pair, FakeVoiceSession, run bridge, send a JSON media frame
        // Assert: FakeVoiceSession.SentAudio[0] equals decoded bytes
        // Implementation note: write a TestWebSocket helper that exposes Send/Receive both ways
        // ... see DuplexTestWebSocket helper in this file's appendix (add to Fakes/) ...
    }

    [Fact]
    public async Task OutboundTrack_Filtered_NotSentToSession()
    {
        // Send a frame with track="outbound" → SentAudio remains empty
    }

    [Fact]
    public async Task DtmfEvent_Ignored_NoCrash()
    {
        // Send DTMF frame → bridge logs and continues
    }
}
```

(Concrete code for `DuplexTestWebSocket` is required since `WebSocket` is abstract; build a minimal pair where `Server.SendAsync` enqueues bytes for `Client.ReceiveAsync`. Roughly 40 lines; see existing helpers in the codebase via `grep -rn "WebSocket" OpenAgent.Tests/` and follow the pattern.)

- [ ] **Step 3: Run, expect FAIL.**

- [ ] **Step 4: Implement read+write loops in the bridge (skeleton, no barge-in/thinking yet).**

```csharp
// TelnyxMediaBridge.cs — replace stub
using System.Buffers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using OpenAgent.Contracts;
using OpenAgent.Models.Voice;

namespace OpenAgent.Channel.Telnyx;

public sealed class TelnyxMediaBridge : IAsyncDisposable
{
    private readonly TelnyxChannelProvider _provider;
    private readonly PendingBridge _pending;
    private readonly WebSocket _ws;
    private readonly ILogger<TelnyxMediaBridge> _logger;
    private readonly CancellationTokenSource _cts;
    private IVoiceSession? _session;

    public TelnyxMediaBridge(
        TelnyxChannelProvider provider,
        PendingBridge pending,
        WebSocket ws,
        ILogger<TelnyxMediaBridge> logger,
        CancellationToken ct)
    {
        _provider = provider;
        _pending = pending;
        _ws = ws;
        _logger = logger;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
    }

    public async Task RunAsync()
    {
        _provider.BridgeRegistry.Register(_pending.ConversationId, this);
        try
        {
            var voiceProvider = _provider.VoiceProviderResolver(_pending.VoiceProviderKey);
            var conversation = _provider.ConversationStore.GetById(_pending.ConversationId)
                ?? throw new InvalidOperationException("Conversation missing");

            _session = await voiceProvider.StartSessionAsync(
                conversation,
                new Models.Voice.VoiceSessionOptions("g711_ulaw", 8000),
                _cts.Token);

            await Task.WhenAny(ReadLoopAsync(_cts.Token), WriteLoopAsync(_cts.Token));
        }
        catch (Exception ex) { _logger.LogError(ex, "Telnyx bridge errored"); }
        finally
        {
            await _cts.CancelAsync();
            if (_session is not null) await _session.DisposeAsync();
            _provider.BridgeRegistry.Unregister(_pending.ConversationId);
        }
    }

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            while (_ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var sb = new StringBuilder();
                WebSocketReceiveResult result;
                do
                {
                    result = await _ws.ReceiveAsync(buffer, ct);
                    if (result.MessageType == WebSocketMessageType.Close) return;
                    sb.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                } while (!result.EndOfMessage);

                TelnyxMediaFrame frame;
                try { frame = TelnyxMediaFrame.Parse(sb.ToString()); }
                catch (JsonException ex) { _logger.LogWarning(ex, "Telnyx malformed frame"); continue; }

                switch (frame.Event)
                {
                    case "media" when frame.Media is { Track: "inbound" }:
                        await _session!.SendAudioAsync(frame.Media.PayloadBytes, ct);
                        break;
                    case "media":
                        // outbound or unknown track — ignore (defensive against misconfigured stream_track)
                        break;
                    case "dtmf":
                        _logger.LogDebug("DTMF digit {Digit}", frame.Dtmf?.Digit);
                        break;
                    case "stop":
                        return;
                    case "start":
                        _logger.LogInformation("Telnyx stream started, format={Encoding}/{Rate}",
                            frame.Start?.MediaFormat.Encoding, frame.Start?.MediaFormat.SampleRate);
                        break;
                }
            }
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }

    private async Task WriteLoopAsync(CancellationToken ct)
    {
        await foreach (var evt in _session!.ReceiveEventsAsync(ct))
        {
            if (_ws.State != WebSocketState.Open) break;
            switch (evt)
            {
                case AudioDelta audio:
                    var json = TelnyxMediaFrame.ComposeMedia(audio.Audio.Span);
                    await _ws.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, ct);
                    break;
                // Tasks 19-22 add: SpeechStarted (barge-in), VoiceToolCallStarted (thinking), AudioDone (hangup), etc.
            }
        }
    }

    public ValueTask DisposeAsync() { _cts.Dispose(); return ValueTask.CompletedTask; }
}
```

- [ ] **Step 5: Run tests, expect PASS.**

- [ ] **Step 6: Commit.**

```bash
git add src/agent/OpenAgent.Channel.Telnyx/TelnyxMediaBridge.cs \
        src/agent/OpenAgent.Tests/Fakes/FakeVoiceSession.cs \
        src/agent/OpenAgent.Tests/TelnyxMediaBridgeReadLoopTests.cs \
        src/agent/OpenAgent.Tests/TelnyxMediaBridgeWriteLoopTests.cs
git commit -m "feat(telnyx): media bridge audio passthrough (read + write loops)"
```

---

## Task 19: Bridge — barge-in (SpeechStarted → clear + cancel)

- [ ] **Step 1: Write the failing test in `TelnyxMediaBridgeBargeInTests.cs`.**

```csharp
[Fact]
public async Task SpeechStarted_SendsClearFrame_AndCancelsResponse()
{
    var (bridge, ws, session) = MakeBridge();
    _ = bridge.RunAsync();
    session.Emit(new SpeechStarted());

    var clearJson = await ws.WaitForOutboundMessageAsync();
    Assert.Contains("\"event\":\"clear\"", clearJson);
    Assert.True(session.CancelCalled);
}
```

- [ ] **Step 2: Add the case to `WriteLoopAsync`.**

```csharp
case SpeechStarted:
    await SendTextAsync(TelnyxMediaFrame.ComposeClear(), ct);
    await _session.CancelResponseAsync(ct);
    break;
```

Add a tiny `SendTextAsync` helper to centralise the framing:

```csharp
private Task SendTextAsync(string json, CancellationToken ct) =>
    _ws.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, ct);
```

- [ ] **Step 3: Run, expect PASS. Commit.**

```bash
git commit -am "feat(telnyx): barge-in via clear + CancelResponseAsync on SpeechStarted"
```

---

## Task 20: Bridge — thinking pump

**Files:**

- Modify: `src/agent/OpenAgent.Channel.Telnyx/TelnyxMediaBridge.cs`
- Test: `src/agent/OpenAgent.Tests/TelnyxMediaBridgeThinkingPumpTests.cs`

- [ ] **Step 1: Write the failing tests.**

```csharp
[Fact]
public async Task VoiceToolCallStarted_StartsPump_FramesFlow()
{
    var (bridge, ws, session) = MakeBridge();
    _ = bridge.RunAsync();
    session.Emit(new VoiceToolCallStarted("web_fetch", "call-1"));

    // Wait briefly for first pump frame
    var msg = await ws.WaitForOutboundMessageAsync(TimeSpan.FromMilliseconds(200));
    Assert.Contains("\"event\":\"media\"", msg);
}

[Fact]
public async Task VoiceToolCallCompleted_StopsPump_AndSendsClear()
{
    var (bridge, ws, session) = MakeBridge();
    _ = bridge.RunAsync();
    session.Emit(new VoiceToolCallStarted("web_fetch", "call-1"));
    await Task.Delay(100);
    session.Emit(new VoiceToolCallCompleted("call-1", "ok"));
    var clearMsg = await ws.WaitForOutboundUntilAsync(s => s.Contains("\"event\":\"clear\""));
    Assert.Contains("\"event\":\"clear\"", clearMsg);
}
```

- [ ] **Step 2: Run, expect FAIL.**

- [ ] **Step 3: Implement the thinking pump.**

Add fields and methods to the bridge:

```csharp
private CancellationTokenSource? _pumpCts;
private int _activeToolCalls;

private void StartPump(CancellationToken ct)
{
    if (Interlocked.Increment(ref _activeToolCalls) > 1) return; // already running
    _pumpCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
    _ = Task.Run(() => PumpAsync(_pumpCts.Token));
}

private async Task StopPumpAsync(CancellationToken ct)
{
    if (Interlocked.Decrement(ref _activeToolCalls) > 0) return; // still busy
    if (_pumpCts is { } cts) await cts.CancelAsync();
    if (_ws.State == WebSocketState.Open) await SendTextAsync(TelnyxMediaFrame.ComposeClear(), ct);
}

private async Task PumpAsync(CancellationToken ct)
{
    var clip = _provider.ThinkingClip;
    var pos = 0;
    const int frameSize = 160; // 20 ms at 8 kHz µ-law
    var period = TimeSpan.FromMilliseconds(20);

    using var timer = new PeriodicTimer(period);
    while (await timer.WaitForNextTickAsync(ct))
    {
        if (_ws.State != WebSocketState.Open) break;
        var slice = clip.AsMemory(pos, Math.Min(frameSize, clip.Length - pos));
        await SendTextAsync(TelnyxMediaFrame.ComposeMedia(slice.Span), ct);
        pos = (pos + frameSize) % clip.Length;
    }
}
```

Add the cases to `WriteLoopAsync`:

```csharp
case VoiceToolCallStarted:
    StartPump(ct);
    break;
case VoiceToolCallCompleted:
    await StopPumpAsync(ct);
    break;
```

- [ ] **Step 4: Run, expect PASS. Commit.**

```bash
git commit -am "feat(telnyx): thinking pump pushes µ-law clip during tool execution"
```

---

## Task 21: Bridge — agent-initiated hangup state machine

**Files:**

- Modify: `src/agent/OpenAgent.Channel.Telnyx/TelnyxMediaBridge.cs`
- Test: `src/agent/OpenAgent.Tests/TelnyxMediaBridgeHangupTests.cs`

- [ ] **Step 1: Write the failing tests covering each branch.**

```csharp
[Fact]
public async Task PendingHangup_AfterAudioDelta_HangsUpOnAudioDone()
{
    var (bridge, _, session, callControl) = MakeBridge();
    _ = bridge.RunAsync();
    bridge.SetPendingHangup();
    session.Emit(new AudioDelta(new byte[] {0,1,2}));
    await Task.Delay(20);
    session.Emit(new AudioDone());
    await callControl.WaitHangupAsync(TimeSpan.FromSeconds(1));
    Assert.True(callControl.WasHangupCalled);
}

[Fact]
public async Task PendingHangup_NoAudioInFlight_HangsUpAfter500ms()
{
    var (bridge, _, _, callControl) = MakeBridge();
    _ = bridge.RunAsync();
    bridge.SetPendingHangup();
    // Don't emit AudioDelta — should hang up after the 500 ms early-exit window
    await callControl.WaitHangupAsync(TimeSpan.FromSeconds(2));
    Assert.True(callControl.WasHangupCalled);
}

[Fact]
public async Task PendingHangup_ModelMisbehaves_HangsUpAfter5s_Hard()
{
    // Emit AudioDelta but never AudioDone — fallback fires at 5 s
    var (bridge, _, session, callControl) = MakeBridge();
    _ = bridge.RunAsync();
    bridge.SetPendingHangup();
    session.Emit(new AudioDelta(new byte[] {0}));
    // No AudioDone — wait for 5 s timer
    await callControl.WaitHangupAsync(TimeSpan.FromSeconds(7));
    Assert.True(callControl.WasHangupCalled);
}
```

(`MakeBridge` factory returns the bridge with a fake `TelnyxCallControlClient` that records `WasHangupCalled`. Adjust if the real provider construction is heavier — wrap in test helper.)

- [ ] **Step 2: Implement the state machine in the bridge.**

```csharp
private bool _pendingHangup;
private bool _audioObservedSinceFlag;
private CancellationTokenSource? _hangupTimerCts;

public void SetPendingHangup()
{
    _pendingHangup = true;
    _audioObservedSinceFlag = false;
    _hangupTimerCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
    _ = HangupTimerAsync(_hangupTimerCts.Token);
}

private async Task HangupTimerAsync(CancellationToken ct)
{
    try
    {
        await Task.Delay(TimeSpan.FromMilliseconds(500), ct);
        if (_pendingHangup && !_audioObservedSinceFlag) { await DoHangupAsync(); return; }

        await Task.Delay(TimeSpan.FromMilliseconds(4500), ct);
        if (_pendingHangup) await DoHangupAsync();
    }
    catch (OperationCanceledException) { /* AudioDone path completed first */ }
}

private async Task DoHangupAsync()
{
    if (!_pendingHangup) return;
    _pendingHangup = false;
    try { await _provider.CallControlClient.HangupAsync(_pending.CallControlId, default); }
    catch (Exception ex) { _logger.LogWarning(ex, "HangupAsync failed"); }
    finally { try { _hangupTimerCts?.Cancel(); } catch { } }
}
```

Add cases in `WriteLoopAsync`:

```csharp
case AudioDelta audio:
    if (_pendingHangup) _audioObservedSinceFlag = true;
    var json = TelnyxMediaFrame.ComposeMedia(audio.Audio.Span);
    await SendTextAsync(json, ct);
    break;

case AudioDone:
    if (_pendingHangup && _audioObservedSinceFlag)
    {
        _hangupTimerCts?.Cancel();
        await DoHangupAsync();
    }
    break;
```

(`AudioDone` event must exist in `OpenAgent.Models/Voice/VoiceEvents.cs`; if not, add `public sealed record AudioDone() : VoiceEvent;` and have providers emit it on `response.audio.done`.)

- [ ] **Step 3: Run, expect PASS.**

- [ ] **Step 4: Commit.**

```bash
git commit -am "feat(telnyx): agent-initiated hangup state machine (early-exit + 5s fallback)"
```

---

## Task 22: Bridge — caller-hangup teardown integration test

- [ ] **Step 1: Write the failing test.**

```csharp
[Fact]
public async Task WsClose_DisposesSession_AndExitsRunAsync()
{
    var (bridge, ws, session, _) = MakeBridge();
    var task = bridge.RunAsync();
    await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, null, default);
    await Task.WhenAny(task, Task.Delay(2000));
    Assert.True(task.IsCompleted);
}
```

- [ ] **Step 2: Verify the existing `RunAsync.finally` already disposes session and unregisters.**

If yes, the test passes immediately. If not, fix.

- [ ] **Step 3: Commit.**

```bash
git commit -am "test(telnyx): caller-hangup teardown coverage"
```

---

## Task 23: EndCallTool

**Files:**

- Create: `src/agent/OpenAgent.Channel.Telnyx/EndCallTool.cs`
- Test: `src/agent/OpenAgent.Tests/TelnyxEndCallToolTests.cs`

- [ ] **Step 1: Write the failing tests.**

```csharp
public class TelnyxEndCallToolTests
{
    [Fact]
    public async Task NonPhoneConversation_ReturnsError()
    {
        var registry = new TelnyxBridgeRegistry();
        var store = new InMemoryConversationStore();
        store.GetOrCreate("c1","app", ConversationType.Voice, "p","m");
        var tool = new EndCallTool(registry, store);
        var result = await tool.ExecuteAsync("{}", "c1", default);
        Assert.Contains("phone", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task NoActiveBridge_ReturnsError()
    {
        var registry = new TelnyxBridgeRegistry();
        var store = new InMemoryConversationStore();
        store.GetOrCreate("c1","telnyx", ConversationType.Phone, "p","m");
        var tool = new EndCallTool(registry, store);
        var result = await tool.ExecuteAsync("{}", "c1", default);
        Assert.Contains("no active call", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ActiveBridge_SetsPendingHangup_AndReturnsOk()
    {
        var registry = new TelnyxBridgeRegistry();
        var fakeBridge = new FakeBridge();
        registry.Register("c1", fakeBridge);
        var store = new InMemoryConversationStore();
        store.GetOrCreate("c1","telnyx", ConversationType.Phone, "p","m");
        var tool = new EndCallTool(registry, store);
        var result = await tool.ExecuteAsync("{}", "c1", default);
        Assert.Equal("ok", result);
        Assert.True(fakeBridge.HangupRequested);
    }

    private sealed class FakeBridge { public bool HangupRequested; public void SetPendingHangup() => HangupRequested = true; }
}
```

- [ ] **Step 2: Run, expect FAIL.**

- [ ] **Step 3: Implement the tool.**

```csharp
// OpenAgent.Channel.Telnyx/EndCallTool.cs
using OpenAgent.Contracts;
using OpenAgent.Models.Conversations;

namespace OpenAgent.Channel.Telnyx;

public sealed class EndCallTool : ITool
{
    private readonly TelnyxBridgeRegistry _registry;
    private readonly IConversationStore _store;

    public AgentToolDefinition Definition { get; } = new()
    {
        Name = "end_call",
        Description = "Politely end the current phone call. Use only after speaking a brief farewell. The line drops after the farewell finishes playing.",
        Parameters = """{"type":"object","properties":{},"additionalProperties":false}"""
    };

    public EndCallTool(TelnyxBridgeRegistry registry, IConversationStore store)
    {
        _registry = registry;
        _store = store;
    }

    public Task<string> ExecuteAsync(string arguments, string conversationId, CancellationToken ct = default)
    {
        var conv = _store.GetById(conversationId);
        if (conv is null || conv.Type != ConversationType.Phone)
            return Task.FromResult("error: end_call only works during a phone call");

        if (!_registry.TryGet(conversationId, out var bridge) || bridge is null)
            return Task.FromResult("error: no active call to end");

        // The bridge is registered as object; reflectively invoke SetPendingHangup() to keep this
        // file decoupled from TelnyxMediaBridge's specific type. (A typed cast also works — pick
        // whichever your codebase prefers.)
        var method = bridge.GetType().GetMethod("SetPendingHangup");
        method?.Invoke(bridge, null);
        return Task.FromResult("ok");
    }
}
```

(If reflection feels heavy, change the registry to be typed: `Register(string, ITelnyxBridge)` where `ITelnyxBridge` exposes `void SetPendingHangup()`. The reflection-free version is preferable; introduce the interface in this task if it doesn't exist yet.)

- [ ] **Step 4: Run, expect PASS.**

- [ ] **Step 5: Register the tool through an `IToolHandler` (mirroring the pattern of FileSystem/Shell handlers).**

```csharp
// OpenAgent.Channel.Telnyx/TelnyxToolHandler.cs (new)
using OpenAgent.Contracts;

namespace OpenAgent.Channel.Telnyx;

public sealed class TelnyxToolHandler : IToolHandler
{
    public string Capability => "telnyx";
    public IReadOnlyList<ITool> Tools { get; }

    public TelnyxToolHandler(EndCallTool endCall) { Tools = [endCall]; }
}
```

- [ ] **Step 6: Commit.**

```bash
git add src/agent/OpenAgent.Channel.Telnyx/EndCallTool.cs \
        src/agent/OpenAgent.Channel.Telnyx/TelnyxToolHandler.cs \
        src/agent/OpenAgent.Tests/TelnyxEndCallToolTests.cs
git commit -m "feat(telnyx): end_call tool with phone-only gating"
```

---

## Task 24: Wire Telnyx into Program.cs

**Files:**

- Modify: `src/agent/OpenAgent/Program.cs`

- [ ] **Step 1: Add the using and registrations.**

```csharp
using OpenAgent.Channel.Telnyx;

// ... in service registrations, after the existing voice provider registrations ...

builder.Services.AddSingleton<TelnyxBridgeRegistry>();
builder.Services.AddSingleton<EndCallTool>();
builder.Services.AddSingleton<IToolHandler, TelnyxToolHandler>();

// Voice provider resolver (mirrors the existing text-provider resolver pattern)
builder.Services.AddSingleton<Func<string, ILlmVoiceProvider>>(sp => key =>
    sp.GetRequiredKeyedService<ILlmVoiceProvider>(key));

builder.Services.AddHttpClient(nameof(TelnyxCallControlClient));

builder.Services.AddSingleton<IChannelProviderFactory>(sp =>
    new TelnyxChannelProviderFactory(
        sp.GetRequiredService<IConversationStore>(),
        sp.GetRequiredService<IConnectionStore>(),
        sp.GetRequiredService<Func<string, ILlmVoiceProvider>>(),
        sp.GetRequiredService<AgentConfig>(),
        sp.GetRequiredService<TelnyxBridgeRegistry>(),
        sp.GetRequiredService<IHttpClientFactory>(),
        sp.GetRequiredService<ILoggerFactory>()));

// ... after the existing app.Map* calls ...
app.MapTelnyxWebhookEndpoints();
app.MapTelnyxStreamingEndpoint();
```

If `Func<string, ILlmTextProvider>` is already registered with the same shape, just add the voice-provider analogue.

- [ ] **Step 2: Build the host project.**

```bash
dotnet build OpenAgent/OpenAgent.csproj
```

Expected: succeeds.

- [ ] **Step 3: Run a smoke test — start the host, hit `/health`.**

```bash
dotnet run --project OpenAgent &
sleep 3
curl -i http://localhost:5264/health
kill %1
```

Expected: 200 OK with `{"status":"ok",...}`. Telnyx connection isn't enabled (none in `connections.json` initially), so no Telnyx-specific behaviour yet.

- [ ] **Step 4: Run all tests.**

```bash
dotnet test
```

Expected: all green. Integration tests for the webhook and streaming endpoints (Tasks 16/17) now hit a real wired-up app.

- [ ] **Step 5: Commit.**

```bash
git commit -am "feat(telnyx): DI wiring for channel + tool handler + endpoints"
```

---

## Task 25: Cancellation audit on existing tools

**Files:**

- Modify: `src/agent/OpenAgent.Tools.WebFetch/WebFetchTool.cs`
- Modify: `src/agent/OpenAgent.Tools.Shell/ShellExecTool.cs`
- Audit (read only): `src/agent/OpenAgent.MemoryIndex/MemoryToolHandler.cs`

- [ ] **Step 1: Inspect the existing tools for `CancellationToken` propagation.**

```bash
grep -n 'CancellationToken\|HttpClient\|Process\.' OpenAgent.Tools.WebFetch/WebFetchTool.cs OpenAgent.Tools.Shell/ShellExecTool.cs | head -50
```

For each tool that calls a network/process API, confirm the `ct` from `ExecuteAsync` is forwarded.

- [ ] **Step 2: For `WebFetchTool`, ensure `ct` reaches the HTTP call.**

```csharp
// before
var response = await _http.SendAsync(req);
// after
var response = await _http.SendAsync(req, ct);
```

- [ ] **Step 3: For `ShellExecTool`, ensure `ct` cancels the process.**

The existing tool likely uses `Process.WaitForExitAsync(ct)`. Confirm; if not, add. Also wire `ct.Register(() => process.Kill(entireProcessTree: true))`.

- [ ] **Step 4: Add a small test per tool that asserts cancellation.**

```csharp
// OpenAgent.Tests/WebFetchToolCancellationTests.cs
[Fact]
public async Task WebFetch_HonoursCancellation()
{
    var tool = MakeWebFetchTool(handler: _ => Task.Delay(5000)); // slow handler
    var cts = new CancellationTokenSource();
    var task = tool.ExecuteAsync("""{"url":"https://example.com"}""", "c1", cts.Token);
    cts.CancelAfter(50);
    var result = await task;
    Assert.Contains("cancel", result, StringComparison.OrdinalIgnoreCase);
}
```

- [ ] **Step 5: Run tests, expect PASS.**

- [ ] **Step 6: Commit.**

```bash
git add src/agent/OpenAgent.Tools.WebFetch/ src/agent/OpenAgent.Tools.Shell/ src/agent/OpenAgent.Tests/WebFetchToolCancellationTests.cs src/agent/OpenAgent.Tests/ShellExecToolCancellationTests.cs
git commit -m "fix(tools): honour CancellationToken in network/process tools for phone-call abort"
```

---

## Task 26: Manual end-to-end test

> No code change — operational checklist. Run AFTER PR3 merges and the host is deployed (or running through the devtunnel).

- [ ] In OpenAgent settings UI, edit the Telnyx connection:
  - Set `phoneNumber` to `+4535150636` (the actual number on the account, not the `+4535150635` typo from the prior P2 run).
  - Confirm `callControlAppId` matches the Call Control connection ID in the Telnyx portal — `2937009616636086168` per the API check earlier.
  - Paste the ED25519 public key into `webhookPublicKey` (Telnyx Dev Hub → Webhook signing).
  - Set `allowedNumbers` to your own caller number while testing.
- [ ] Enable the connection and start the agent. The host log should include `Telnyx [<id>] started: phone=+4535150636, webhookId=<12-hex>, allow=1`. Note the webhookId.
- [ ] In the Telnyx portal, confirm the Call Control connection's webhook URL is `{baseUrl}/api/webhook/telnyx/{webhookId}/call`. Save and assign the phone number.
- [ ] Place a call to `+4535150636` from the allowed number. Expected:
  - Greeting + agent voice within ~1 s of pickup.
  - Free-form conversation; agent responds in real time.
  - Tool call (e.g. ask "what's the weather in Aarhus right now"): caller hears the procedural ambient clip during the `web_fetch`, then the agent's response.
  - Caller speaks while the agent is mid-sentence: agent stops within ~200 ms (barge-in).
  - Caller says "thanks, goodbye": agent responds with a brief farewell, then the line drops within 5 s.
  - After the call, the conversation in the OpenAgent UI shows the transcript persisted as messages.
- [ ] Place a second call from the same number a few minutes later: agent should reference prior context (transcript-replay).
- [ ] Place a call from a non-allowlisted number: line drops immediately (no greeting).

If any step fails, gather logs from `{dataPath}/logs/log-{date}.jsonl` filtered to `Telnyx` for the call's time window.

---

## Self-Review Pass

(Run after writing the plan, before handoff.)

- [ ] Spec coverage: each spec section maps to a task.
  - Goal/Scope → Tasks 1–24 collectively
  - Architecture diagram → Tasks 16–18 implement it
  - Components table → Tasks 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 23
  - Data flow → Task 16 (steps 1–7), Task 17 (step 8), Task 18 (steps 9–11), Task 21 (steps 13–14)
  - Configuration surface → Task 15
  - Telnyx portal setup → Task 26 (operational checklist)
  - Audio format rule → Task 12 (streaming_start params), Task 18 (passthrough)
  - Thinking-clip mechanism → Task 13 (factory), Task 20 (pump)
  - Barge-in → Task 19
  - Conversation history → Task 5 (Phone enum + PHONE.md), Task 16 (E.164 lookup)
  - Allowlist + signature → Task 10 (verifier), Task 16 (allowlist gate)
  - Per-call ephemeral state → Task 14 (registry), Task 15 (pending dict), Task 16 (eviction)
  - WS endpoint trust model → Task 17 (?call= validation)
  - Agent-initiated hangup state machine → Task 21
  - Cancellation during tool execution → Task 25
  - Conversation-history replay cost → no code change (covered by existing voice provider behaviour)
  - Browser thinking-cue protocol → Task 6
- [ ] Placeholder scan: no "TBD", "implement later", or "similar to Task N" without code.
- [ ] Type consistency: `VoiceSessionOptions(Codec, SampleRate)` consistent across Tasks 1–4. `PendingBridge` consistent in 15–17. `SetPendingHangup` named identically in 21 and 23.

---

## Execution Handoff

Plan complete and saved to `docs/superpowers/plans/2026-04-25-telnyx-realtime-voice.md`. Two execution options:

**1. Subagent-Driven (recommended)** — I dispatch a fresh subagent per task, review between tasks, fast iteration.

**2. Inline Execution** — Execute tasks in this session using executing-plans, batch execution with checkpoints.

Which approach?
