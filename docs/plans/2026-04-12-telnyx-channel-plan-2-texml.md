# Telnyx Channel — Plan 2: TeXML Inbound Voice

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Answer inbound phone calls end-to-end using Telnyx's TeXML application mode and built-in STT/TTS — caller hears a greeting, speaks, the agent responds in its own voice, conversation persists across turns and across calls from the same number.

**Architecture:** Telnyx posts form-encoded webhooks to `POST /api/webhook/telnyx/{webhookId}/voice` (initial call) and `POST /api/webhook/telnyx/{webhookId}/speech` (subsequent turns after `<Gather input="speech">`). The app validates the ED25519 signature, resolves the running `TelnyxChannelProvider` via `ConnectionManager`, enforces the caller allowlist, derives a conversation via `FindOrCreateChannelConversation("telnyx", connectionId, e164, …, ConversationType.Phone)`, calls the configured `ILlmTextProvider`, and returns a TeXML `<Response>` with `<Say>` + `<Gather>` (or `<Hangup>` when the caller ends the call). No real-time audio, no media streaming — Telnyx handles all audio rendering and recognition.

**Tech Stack:** .NET 10, ASP.NET Core minimal APIs, `System.Security.Cryptography.Ed25519` (built-in since .NET 10), System.Text.Json, xUnit + `WebApplicationFactory<Program>`. No new NuGet packages required.

---

## File Structure

| File | Responsibility |
|---|---|
| `src/agent/OpenAgent.Models/Conversations/Conversation.cs` | Add `Phone = 2` to `ConversationType` enum |
| `src/agent/OpenAgent/SystemPromptBuilder.cs` | Add `("PHONE.md", [ConversationType.Phone])` to `FileMap` |
| `src/agent/OpenAgent/Resources/defaults/PHONE.md` | Default phone-call system prompt content |
| `src/agent/OpenAgent/DataDirectoryBootstrap.cs` | Extract `PHONE.md` to `{dataPath}` on first run |
| `src/agent/OpenAgent.Channel.Telnyx/TelnyxOptions.cs` | Add `WebhookId`, `WebhookPublicKey`, `BaseUrl` |
| `src/agent/OpenAgent.Channel.Telnyx/TelnyxChannelProviderFactory.cs` | Parse new fields; auto-generate `WebhookId` on first start |
| `src/agent/OpenAgent.Channel.Telnyx/TelnyxChannelProvider.cs` | Expose `WebhookId`; surface the signature verifier and handler instances |
| `src/agent/OpenAgent.Channel.Telnyx/TelnyxSignatureVerifier.cs` | ED25519 verification of `Telnyx-Signature-ed25519` + `Telnyx-Timestamp` over `{timestamp}|{body}` |
| `src/agent/OpenAgent.Channel.Telnyx/TeXmlBuilder.cs` | Compose TeXML `<Response>` strings; XML-escape user text |
| `src/agent/OpenAgent.Channel.Telnyx/TelnyxMessageHandler.cs` | Handle voice/speech webhooks; map to conversation; call text provider; return TeXML |
| `src/agent/OpenAgent.Channel.Telnyx/TelnyxWebhookEndpoints.cs` | Minimal API endpoints: `MapTelnyxWebhookEndpoints(this WebApplication)` |
| `src/agent/OpenAgent/Program.cs` | Call `app.MapTelnyxWebhookEndpoints()` |
| `src/agent/OpenAgent.Tests/TelnyxSignatureVerifierTests.cs` | Unit tests for signature verifier |
| `src/agent/OpenAgent.Tests/TeXmlBuilderTests.cs` | Unit tests for XML generation |
| `src/agent/OpenAgent.Tests/TelnyxMessageHandlerTests.cs` | Unit tests for conversation/allowlist flow |
| `src/agent/OpenAgent.Tests/TelnyxWebhookEndpointTests.cs` | Integration tests via `WebApplicationFactory<Program>` |
| `src/agent/OpenAgent.Tests/Fakes/FakeTelnyxTextProvider.cs` | Test double for `ILlmTextProvider` — returns canned replies |

---

## Key contracts and assumptions

**Telnyx webhook payload (form-encoded, not JSON).** TeXML Applications post `application/x-www-form-urlencoded`. The fields we use:

| Field | Purpose |
|---|---|
| `CallSid` / `CallSidLegacy` / `CallControlId` | Unique call identifier (we use whichever is populated) |
| `From` | Caller E.164 number |
| `To` | The number dialled (our Telnyx number) |
| `CallStatus` | `ringing`, `in-progress`, `completed`, `failed` |
| `SpeechResult` | Transcribed speech from `<Gather input="speech">` (empty if no speech detected) |
| `Digits` | DTMF digits (ignored in plan 2 — plan 3 territory) |

**Telnyx signature verification.** Each webhook carries:
- `Telnyx-Signature-ed25519` header — base64-encoded signature
- `Telnyx-Timestamp` header — unix timestamp (seconds)
- Signed payload is UTF-8 `{timestamp}|{raw-request-body}`
- ED25519 public key is copied from the Telnyx portal into `TelnyxOptions.WebhookPublicKey` (PEM-encoded)
- Anti-replay: reject if `|now - timestamp| > 300s`

**Conversation mapping.** `FindOrCreateChannelConversation("telnyx", connectionId, callerE164, "telnyx", ConversationType.Phone, provider, model)`. Same E.164 → same conversation across multiple calls. DisplayName set to the E.164 for visibility in the Conversations UI.

**Allowlist.** `TelnyxOptions.AllowedNumbers` — empty list means allow all (matches what the docstring now says post-plan-1). Non-empty list: caller must match an entry exactly; mismatched caller gets a `<Say>Not authorised.</Say><Hangup/>` response.

**Provider resolution.** Text provider is resolved per-message via the existing `Func<string, ILlmTextProvider>` pattern, using the conversation's stored `Provider` name — matches Telegram exactly.

**What this plan does NOT do** (by design — later plans):
- Real-time audio, Media Streaming, ILlmVoiceProvider integration — plan 3
- Outbound calls / IOutboundSender — plan 4
- DTMF input handling — plan 3 (input="speech" only in plan 2)
- Barge-in — plan 3 (TeXML's `<Gather>` is turn-based; barge-in belongs to real-time audio)
- Phone-specific tools like `end_call`, `transfer_call` — plan 3
- Per-conversation-type tool allowlist — plan 3
- Call recording — plan 4 or separate follow-up

---

## Task 1: Add `ConversationType.Phone` + wire the system prompt

**Files:**
- Modify: `src/agent/OpenAgent.Models/Conversations/Conversation.cs`
- Modify: `src/agent/OpenAgent/SystemPromptBuilder.cs`
- Create: `src/agent/OpenAgent/Resources/defaults/PHONE.md`
- Modify: `src/agent/OpenAgent/DataDirectoryBootstrap.cs`
- Test: `src/agent/OpenAgent.Tests/SystemPromptBuilderTests.cs` (new or append to existing)

- [ ] **Step 1: Add `Phone` to the enum**

Open `src/agent/OpenAgent.Models/Conversations/Conversation.cs`. The enum currently has only `Text` and `Voice`. Add `Phone` as the third value:

```csharp
public enum ConversationType
{
    Text,
    Voice,
    Phone,
}
```

- [ ] **Step 2: Create the default PHONE.md**

Find where default markdown prompts live by running:

```bash
find src/agent/OpenAgent/Resources/defaults -type f -name "*.md" 2>/dev/null
```

If the folder exists, write `src/agent/OpenAgent/Resources/defaults/PHONE.md`. If it does NOT exist, check how `DataDirectoryBootstrap` currently locates defaults (it may embed them as `EmbeddedResource` in the csproj or copy-on-build) — follow the same pattern. A reasonable default content:

```markdown
# Phone Call Etiquette

You are speaking with a caller over a regular phone call. The audio round-trip
goes through Telnyx, which transcribes what the caller says and speaks your
replies back to them using a synthesised voice.

**Keep replies short.** One or two sentences per turn. Long paragraphs sound
robotic when read aloud and waste the caller's time.

**Speak naturally.** Avoid bullet lists, code blocks, or markdown headings in
your replies — they will be spoken verbatim and sound odd. Prefer full sentences.

**Watch for silence.** If the caller says nothing (empty `SpeechResult`), assume
they did not hear you. Repeat your last sentence or ask a clarifying question.

**End the call when the caller signals done.** If they say goodbye, thanks,
or "that's all", give a short farewell and expect the call to end.

**You cannot see anything.** There is no screen, no images, no files visible
to the caller. Do not offer to "show" or "display" anything.
```

If `Resources/defaults/` does not already exist in the project structure, treat this as creating a new folder: add the folder, write the file, and in the next step ensure `DataDirectoryBootstrap` knows about it.

- [ ] **Step 3: Wire the FileMap**

Open `src/agent/OpenAgent/SystemPromptBuilder.cs`. Find the `FileMap` tuple array (around lines 23–32 per the grounding report). Add the Phone entry. Final FileMap shape:

```csharp
private static readonly (string File, ConversationType[] Types)[] FileMap =
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

Phone calls use the full persona (AGENTS/SOUL/IDENTITY/USER/TOOLS/MEMORY) plus the phone-specific etiquette. They do NOT get `VOICE.md` — that is for the real-time voice flow which has different affordances.

- [ ] **Step 4: Update `DataDirectoryBootstrap` to extract `PHONE.md`**

Open `src/agent/OpenAgent/DataDirectoryBootstrap.cs`. Locate the list of default markdown files it extracts. Add `"PHONE.md"` to that list (follow the exact idiom — if the existing code is a string array, append; if it's per-file `if (!File.Exists(...)) ExtractResource(...)` blocks, add one more block matching the surrounding style).

Verify the extraction step works: run the app once against a fresh `DATA_DIR` and confirm `{dataPath}/PHONE.md` appears.

- [ ] **Step 5: Write the failing test**

Create or extend `src/agent/OpenAgent.Tests/SystemPromptBuilderTests.cs`:

```csharp
using Microsoft.Extensions.Logging.Abstractions;
using OpenAgent;
using OpenAgent.Models.Conversations;
using Xunit;

namespace OpenAgent.Tests;

public class SystemPromptBuilderTests
{
    [Fact]
    public void Build_for_Phone_type_includes_phone_etiquette()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "openagent-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            File.WriteAllText(Path.Combine(tempDir, "AGENTS.md"), "# Agent baseline");
            File.WriteAllText(Path.Combine(tempDir, "PHONE.md"), "# Phone etiquette");
            File.WriteAllText(Path.Combine(tempDir, "VOICE.md"), "# Voice realtime");

            var builder = new SystemPromptBuilder(tempDir, NullLogger<SystemPromptBuilder>.Instance);

            var phonePrompt = builder.Build(ConversationType.Phone, activeSkills: []);
            var voicePrompt = builder.Build(ConversationType.Voice, activeSkills: []);
            var textPrompt = builder.Build(ConversationType.Text, activeSkills: []);

            Assert.Contains("Phone etiquette", phonePrompt);
            Assert.Contains("Agent baseline", phonePrompt);
            Assert.DoesNotContain("Voice realtime", phonePrompt);

            Assert.DoesNotContain("Phone etiquette", voicePrompt);
            Assert.DoesNotContain("Phone etiquette", textPrompt);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }
}
```

Note: if `SystemPromptBuilder`'s actual constructor signature differs (e.g., takes `IOptions<T>` or `AgentEnvironment` instead of a raw path), adapt the test's construction but keep the assertions identical. Read `SystemPromptBuilder.cs` before writing the test if unsure.

- [ ] **Step 6: Run the test — confirm it passes**

From `src/agent/`:

```bash
dotnet test OpenAgent.Tests/OpenAgent.Tests.csproj --filter "FullyQualifiedName~SystemPromptBuilderTests"
```

Expected: test passes. If it fails on a construction mismatch, fix the test to match the real constructor signature — do NOT change `SystemPromptBuilder`'s public API for this plan.

- [ ] **Step 7: Commit**

```bash
git add src/agent/OpenAgent.Models/Conversations/Conversation.cs \
        src/agent/OpenAgent/SystemPromptBuilder.cs \
        src/agent/OpenAgent/DataDirectoryBootstrap.cs \
        src/agent/OpenAgent/Resources/defaults/PHONE.md \
        src/agent/OpenAgent.Tests/SystemPromptBuilderTests.cs
git commit -m "feat(conversation-type): add Phone with dedicated system prompt"
```

---

## Task 2: Extend `TelnyxOptions` + factory parsing

**Files:**
- Modify: `src/agent/OpenAgent.Channel.Telnyx/TelnyxOptions.cs`
- Modify: `src/agent/OpenAgent.Channel.Telnyx/TelnyxChannelProviderFactory.cs`
- Modify: `src/agent/OpenAgent.Tests/TelnyxChannelProviderFactoryTests.cs`

- [ ] **Step 1: Add new option fields**

Open `src/agent/OpenAgent.Channel.Telnyx/TelnyxOptions.cs` and add three new properties (place them after `AllowedNumbers`, before the closing brace):

```csharp
/// <summary>
/// Auto-generated GUID identifying this connection's webhook endpoint.
/// Populated on first start if absent; persisted in the connection config.
/// Used in the webhook URL: /api/webhook/telnyx/{WebhookId}/voice.
/// </summary>
public string? WebhookId { get; set; }

/// <summary>
/// PEM-encoded ED25519 public key from the Telnyx portal. Used to verify
/// webhook signatures. When null, signatures are NOT verified — accept only
/// for local development.
/// </summary>
public string? WebhookPublicKey { get; set; }

/// <summary>
/// Public base URL of this OpenAgent instance (e.g. "https://openagent.example.com").
/// Used to build the `action` URL in TeXML Gather verbs so Telnyx knows where
/// to post the next turn's speech result. Required for Telnyx callbacks to work.
/// </summary>
public string? BaseUrl { get; set; }
```

- [ ] **Step 2: Declare the new ConfigFields**

Open `src/agent/OpenAgent.Channel.Telnyx/TelnyxChannelProviderFactory.cs`. Update the `ConfigFields` collection to include the three new settings. Full field list after update:

```csharp
public IReadOnlyList<ProviderConfigField> ConfigFields { get; } =
[
    new() { Key = "apiKey",           Label = "API Key",                        Type = "Secret", Required = true },
    new() { Key = "phoneNumber",      Label = "Phone Number (E.164)",           Type = "String", Required = true },
    new() { Key = "baseUrl",          Label = "Public Base URL",                Type = "String", Required = true },
    new() { Key = "webhookPublicKey", Label = "Webhook Public Key (PEM)",       Type = "Secret" },
    new() { Key = "webhookSecret",    Label = "Webhook Signing Secret",         Type = "Secret" },
    new() { Key = "allowedNumbers",   Label = "Allowed Caller Numbers (comma-separated, empty = allow all)", Type = "String" },
];
```

Note: `webhookId` is deliberately NOT a ConfigField — the settings UI should never surface it (auto-generated, not user-editable). It lives in the config blob but is populated by the provider on first start.

- [ ] **Step 3: Parse the new fields in `Create`**

Still in `TelnyxChannelProviderFactory.cs`, extend the `Create` method's `if (connection.Config.ValueKind == JsonValueKind.Object) { … }` block. Add these property reads alongside the existing ones (order: after `webhookSecret`, before `allowedNumbers`):

```csharp
if (connection.Config.TryGetProperty("baseUrl", out var baseUrlEl) && baseUrlEl.ValueKind == JsonValueKind.String)
    options.BaseUrl = baseUrlEl.GetString();

if (connection.Config.TryGetProperty("webhookPublicKey", out var keyPemEl) && keyPemEl.ValueKind == JsonValueKind.String)
    options.WebhookPublicKey = keyPemEl.GetString();

if (connection.Config.TryGetProperty("webhookId", out var webhookIdEl) && webhookIdEl.ValueKind == JsonValueKind.String)
    options.WebhookId = webhookIdEl.GetString();
```

- [ ] **Step 4: Extend the factory tests**

Open `src/agent/OpenAgent.Tests/TelnyxChannelProviderFactoryTests.cs`. Extend the existing `Create_parses_string_config_into_options` test to include the new fields:

```csharp
[Fact]
public void Create_parses_string_config_into_options()
{
    var factory = new TelnyxChannelProviderFactory(NullLoggerFactory.Instance);
    var config = JsonDocument.Parse("""
        {
            "apiKey": "KEY_abc",
            "phoneNumber": "+4512345678",
            "webhookSecret": "shh",
            "baseUrl": "https://example.com",
            "webhookPublicKey": "-----BEGIN PUBLIC KEY-----\nMCo...\n-----END PUBLIC KEY-----",
            "webhookId": "abc-123",
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
    Assert.Equal("https://example.com", provider.Options.BaseUrl);
    Assert.StartsWith("-----BEGIN PUBLIC KEY-----", provider.Options.WebhookPublicKey);
    Assert.Equal("abc-123", provider.Options.WebhookId);
    Assert.Equal(new[] { "+4511111111", "+4522222222" }, provider.Options.AllowedNumbers);
    Assert.Equal("conn-1", provider.ConnectionId);
}
```

Add a new `[Fact]` verifying the ConfigFields contain the new keys:

```csharp
[Fact]
public void ConfigFields_includes_baseUrl_and_webhookPublicKey()
{
    var factory = new TelnyxChannelProviderFactory(NullLoggerFactory.Instance);
    var keys = factory.ConfigFields.Select(f => f.Key).ToArray();

    Assert.Contains("baseUrl", keys);
    Assert.Contains("webhookPublicKey", keys);

    var baseUrl = factory.ConfigFields.Single(f => f.Key == "baseUrl");
    Assert.True(baseUrl.Required);

    var publicKey = factory.ConfigFields.Single(f => f.Key == "webhookPublicKey");
    Assert.Equal("Secret", publicKey.Type);
    Assert.False(publicKey.Required);
}
```

- [ ] **Step 5: Run all Telnyx factory tests — confirm pass**

```bash
dotnet test src/agent/OpenAgent.Tests/OpenAgent.Tests.csproj --filter "FullyQualifiedName~TelnyxChannelProviderFactoryTests"
```

Expected: 7 tests pass (6 existing + 1 new).

- [ ] **Step 6: Commit**

```bash
git add src/agent/OpenAgent.Channel.Telnyx/TelnyxOptions.cs \
        src/agent/OpenAgent.Channel.Telnyx/TelnyxChannelProviderFactory.cs \
        src/agent/OpenAgent.Tests/TelnyxChannelProviderFactoryTests.cs
git commit -m "feat(telnyx): config fields for webhook URL, public key, webhookId"
```

---

## Task 3: TeXmlBuilder — compose TeXML response strings

**Files:**
- Create: `src/agent/OpenAgent.Channel.Telnyx/TeXmlBuilder.cs`
- Create: `src/agent/OpenAgent.Tests/TeXmlBuilderTests.cs`

- [ ] **Step 1: Write the failing tests**

Create `src/agent/OpenAgent.Tests/TeXmlBuilderTests.cs`:

```csharp
using OpenAgent.Channel.Telnyx;
using Xunit;

namespace OpenAgent.Tests;

public class TeXmlBuilderTests
{
    [Fact]
    public void GreetAndGather_produces_say_plus_gather()
    {
        var xml = TeXmlBuilder.GreetAndGather(
            greeting: "Hi, it's OpenAgent. How can I help?",
            gatherActionUrl: "https://example.com/api/webhook/telnyx/abc/speech",
            language: "en-US");

        Assert.Contains("<?xml version=\"1.0\" encoding=\"UTF-8\"?>", xml);
        Assert.Contains("<Response>", xml);
        Assert.Contains("<Gather input=\"speech\" action=\"https://example.com/api/webhook/telnyx/abc/speech\" method=\"POST\" language=\"en-US\" speechTimeout=\"auto\">", xml);
        Assert.Contains("<Say>Hi, it's OpenAgent. How can I help?</Say>", xml);
        Assert.EndsWith("</Response>", xml.TrimEnd());
    }

    [Fact]
    public void RespondAndGather_includes_agent_reply_inside_gather()
    {
        var xml = TeXmlBuilder.RespondAndGather(
            reply: "The answer is 42.",
            gatherActionUrl: "https://example.com/api/webhook/telnyx/abc/speech",
            language: "en-US");

        Assert.Contains("<Gather", xml);
        Assert.Contains("<Say>The answer is 42.</Say>", xml);
    }

    [Fact]
    public void Farewell_ends_with_hangup()
    {
        var xml = TeXmlBuilder.Farewell("Goodbye.");

        Assert.Contains("<Say>Goodbye.</Say>", xml);
        Assert.Contains("<Hangup />", xml);
        Assert.DoesNotContain("<Gather", xml);
    }

    [Fact]
    public void Reject_says_and_hangs_up()
    {
        var xml = TeXmlBuilder.Reject("Not authorised.");

        Assert.Contains("<Say>Not authorised.</Say>", xml);
        Assert.Contains("<Hangup />", xml);
    }

    [Fact]
    public void Text_is_xml_escaped()
    {
        var xml = TeXmlBuilder.Farewell("Say <hi> & go.");

        Assert.Contains("<Say>Say &lt;hi&gt; &amp; go.</Say>", xml);
        Assert.DoesNotContain("<hi>", xml.Replace("&lt;hi&gt;", ""));
    }

    [Fact]
    public void Action_url_is_attribute_escaped()
    {
        var xml = TeXmlBuilder.GreetAndGather(
            greeting: "Hi",
            gatherActionUrl: "https://example.com/path?a=1&b=2",
            language: "en-US");

        Assert.Contains("action=\"https://example.com/path?a=1&amp;b=2\"", xml);
    }
}
```

- [ ] **Step 2: Run the tests — confirm they fail to compile**

```bash
dotnet test src/agent/OpenAgent.Tests/OpenAgent.Tests.csproj --filter "FullyQualifiedName~TeXmlBuilderTests" 2>&1 | tail -20
```

Expected: compile error — `TeXmlBuilder` not found.

- [ ] **Step 3: Implement TeXmlBuilder**

Create `src/agent/OpenAgent.Channel.Telnyx/TeXmlBuilder.cs`:

```csharp
using System.Net;
using System.Text;

namespace OpenAgent.Channel.Telnyx;

/// <summary>
/// Builds Telnyx TeXML response payloads. TeXML is an XML dialect similar to
/// Twilio TwiML — Telnyx reads the returned XML to drive the call.
/// </summary>
public static class TeXmlBuilder
{
    private const string XmlHeader = "<?xml version=\"1.0\" encoding=\"UTF-8\"?>";

    /// <summary>
    /// Greet the caller and gather their next speech turn.
    /// </summary>
    public static string GreetAndGather(string greeting, string gatherActionUrl, string language = "en-US") =>
        BuildSayInsideGather(greeting, gatherActionUrl, language);

    /// <summary>
    /// Reply with the agent's text then gather the caller's next turn.
    /// Mechanically identical to GreetAndGather today; kept as a named method
    /// so call-site intent is explicit and future divergence (e.g. different
    /// voice for agent vs greeting) is a one-file change.
    /// </summary>
    public static string RespondAndGather(string reply, string gatherActionUrl, string language = "en-US") =>
        BuildSayInsideGather(reply, gatherActionUrl, language);

    /// <summary>
    /// Speak a final line then hang up.
    /// </summary>
    public static string Farewell(string line)
    {
        var sb = new StringBuilder();
        sb.AppendLine(XmlHeader);
        sb.AppendLine("<Response>");
        sb.Append("  <Say>").Append(XmlEscapeText(line)).AppendLine("</Say>");
        sb.AppendLine("  <Hangup />");
        sb.Append("</Response>");
        return sb.ToString();
    }

    /// <summary>
    /// Reject the call with a reason and hang up. Used for allowlist denials.
    /// </summary>
    public static string Reject(string reason) => Farewell(reason);

    private static string BuildSayInsideGather(string line, string gatherActionUrl, string language)
    {
        var sb = new StringBuilder();
        sb.AppendLine(XmlHeader);
        sb.AppendLine("<Response>");
        sb.Append("  <Gather input=\"speech\" action=\"")
          .Append(XmlEscapeAttribute(gatherActionUrl))
          .Append("\" method=\"POST\" language=\"")
          .Append(XmlEscapeAttribute(language))
          .AppendLine("\" speechTimeout=\"auto\">");
        sb.Append("    <Say>").Append(XmlEscapeText(line)).AppendLine("</Say>");
        sb.AppendLine("  </Gather>");
        sb.Append("</Response>");
        return sb.ToString();
    }

    private static string XmlEscapeText(string text) =>
        WebUtility.HtmlEncode(text ?? "");

    private static string XmlEscapeAttribute(string text) =>
        WebUtility.HtmlEncode(text ?? "");
}
```

Note: `WebUtility.HtmlEncode` covers `<`, `>`, `&`, `"`, `'` — sufficient for both text nodes and attribute values in XML where encoded entities are accepted.

- [ ] **Step 4: Run the tests — confirm pass**

```bash
dotnet test src/agent/OpenAgent.Tests/OpenAgent.Tests.csproj --filter "FullyQualifiedName~TeXmlBuilderTests"
```

Expected: 6/6 pass.

- [ ] **Step 5: Commit**

```bash
git add src/agent/OpenAgent.Channel.Telnyx/TeXmlBuilder.cs \
        src/agent/OpenAgent.Tests/TeXmlBuilderTests.cs
git commit -m "feat(telnyx): TeXmlBuilder for greeting, gather, farewell, reject"
```

---

## Task 4: Signature verifier (ED25519)

**Files:**
- Create: `src/agent/OpenAgent.Channel.Telnyx/TelnyxSignatureVerifier.cs`
- Create: `src/agent/OpenAgent.Tests/TelnyxSignatureVerifierTests.cs`

**Note on ED25519 in .NET 10.** The verifier relies on `System.Security.Cryptography.Ed25519` (built-in since .NET 10). Signing and verification APIs share the same PEM format, so the test can generate a keypair, sign a test payload, and assert the verifier accepts it — no network, no Telnyx dependency. If the installed SDK lacks that type (unlikely on .NET 10, but possible on preview builds), add `BouncyCastle.Cryptography` via CPM — alternate BouncyCastle code is shown after the primary implementation below. The test shape is identical for both.

Unlike a typical TDD cycle, the test and implementation are committed together in a single step because the test's `GenerateKeyPair` helper uses the same API surface as the implementation — splitting them would produce a compile-failure red phase that doesn't isolate any useful signal.

- [ ] **Step 1: Confirm .NET 10 has direct Ed25519 support**

Run:

```bash
grep -rn "System.Security.Cryptography.Ed25519\|using System.Security.Cryptography" src/agent 2>/dev/null | head -5
dotnet --info | head -5
```

Proceed with `System.Security.Cryptography.Ed25519` as the default. Only swap to BouncyCastle if the Step 4 build fails with a `CS0246` on that type — in which case use the BouncyCastle variant shown in Step 2.

- [ ] **Step 2: Implement the verifier + write the tests**

Create `src/agent/OpenAgent.Channel.Telnyx/TelnyxSignatureVerifier.cs`:

```csharp
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;

namespace OpenAgent.Channel.Telnyx;

/// <summary>
/// Verifies Telnyx webhook signatures.
///
/// Payload: UTF-8 bytes of <c>{timestamp}|{raw-body}</c>.
/// Header "Telnyx-Signature-ed25519" carries base64-encoded ED25519 signature.
/// Header "Telnyx-Timestamp" carries unix seconds; anti-replay window is 300s.
/// </summary>
public sealed class TelnyxSignatureVerifier
{
    private const int MaxClockSkewSeconds = 300;

    private readonly ILogger<TelnyxSignatureVerifier> _logger;

    public TelnyxSignatureVerifier(ILogger<TelnyxSignatureVerifier> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Verify a signed request.
    /// When publicKeyPem is null or empty, verification is SKIPPED and a warning
    /// is logged — used only in local development.
    /// </summary>
    public bool Verify(
        string? publicKeyPem,
        string? signatureHeader,
        string? timestampHeader,
        byte[] rawBody,
        DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(publicKeyPem))
        {
            _logger.LogWarning("Telnyx signature verification skipped — no public key configured");
            return true;
        }

        if (string.IsNullOrWhiteSpace(signatureHeader) || string.IsNullOrWhiteSpace(timestampHeader))
        {
            _logger.LogWarning("Telnyx webhook missing signature or timestamp header");
            return false;
        }

        if (!long.TryParse(timestampHeader, out var timestamp))
        {
            _logger.LogWarning("Telnyx webhook timestamp is not a valid integer");
            return false;
        }

        var delta = Math.Abs(now.ToUnixTimeSeconds() - timestamp);
        if (delta > MaxClockSkewSeconds)
        {
            _logger.LogWarning("Telnyx webhook timestamp outside clock-skew window ({Delta}s)", delta);
            return false;
        }

        byte[] signatureBytes;
        try
        {
            signatureBytes = Convert.FromBase64String(signatureHeader);
        }
        catch (FormatException)
        {
            _logger.LogWarning("Telnyx webhook signature header is not valid base64");
            return false;
        }

        var payload = new byte[Encoding.UTF8.GetByteCount(timestampHeader) + 1 + rawBody.Length];
        var written = Encoding.UTF8.GetBytes(timestampHeader, payload);
        payload[written] = (byte)'|';
        Buffer.BlockCopy(rawBody, 0, payload, written + 1, rawBody.Length);

        try
        {
            using var alg = System.Security.Cryptography.Ed25519.ImportFromPem(publicKeyPem);
            return alg.VerifyData(payload, signatureBytes);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Telnyx webhook signature verification threw; treating as invalid");
            return false;
        }
    }
}
```

If `System.Security.Cryptography.Ed25519` is unavailable on the target SDK, swap the body of the try block with BouncyCastle equivalent:

```csharp
// BouncyCastle fallback — requires package reference
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using Org.BouncyCastle.OpenSsl;
using System.IO;
using (var sr = new StringReader(publicKeyPem))
{
    var pem = new PemReader(sr);
    var pubKey = (Ed25519PublicKeyParameters)pem.ReadObject();
    var signer = new Ed25519Signer();
    signer.Init(forSigning: false, pubKey);
    signer.BlockUpdate(payload, 0, payload.Length);
    return signer.VerifySignature(signatureBytes);
}
```

Use whichever compiles on the project's .NET 10 SDK. The test below is identical for both.

Now create `src/agent/OpenAgent.Tests/TelnyxSignatureVerifierTests.cs`:

```csharp
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using OpenAgent.Channel.Telnyx;
using Xunit;

namespace OpenAgent.Tests;

public class TelnyxSignatureVerifierTests
{
    private readonly TelnyxSignatureVerifier _verifier = new(NullLogger<TelnyxSignatureVerifier>.Instance);

    [Fact]
    public void Verify_returns_true_when_public_key_is_null()
    {
        var ok = _verifier.Verify(
            publicKeyPem: null,
            signatureHeader: "anything",
            timestampHeader: "1234567890",
            rawBody: Encoding.UTF8.GetBytes("{}"),
            now: DateTimeOffset.UnixEpoch);

        Assert.True(ok);
    }

    [Fact]
    public void Verify_returns_true_for_valid_signature()
    {
        var (publicPem, privatePem) = GenerateKeyPair();
        var body = Encoding.UTF8.GetBytes("""{"event_type":"call.initiated"}""");
        var now = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);
        var timestamp = now.ToUnixTimeSeconds().ToString();
        var signature = SignForTest(privatePem, timestamp, body);

        var ok = _verifier.Verify(publicPem, signature, timestamp, body, now);

        Assert.True(ok);
    }

    [Fact]
    public void Verify_rejects_tampered_body()
    {
        var (publicPem, privatePem) = GenerateKeyPair();
        var body = Encoding.UTF8.GetBytes("""{"event_type":"call.initiated"}""");
        var now = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);
        var timestamp = now.ToUnixTimeSeconds().ToString();
        var signature = SignForTest(privatePem, timestamp, body);

        var tampered = Encoding.UTF8.GetBytes("""{"event_type":"call.HACKED"}""");
        var ok = _verifier.Verify(publicPem, signature, timestamp, tampered, now);

        Assert.False(ok);
    }

    [Fact]
    public void Verify_rejects_stale_timestamp()
    {
        var (publicPem, privatePem) = GenerateKeyPair();
        var body = Encoding.UTF8.GetBytes("{}");
        var stale = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);
        var now = stale.AddMinutes(10);
        var staleTimestamp = stale.ToUnixTimeSeconds().ToString();
        var signature = SignForTest(privatePem, staleTimestamp, body);

        var ok = _verifier.Verify(publicPem, signature, staleTimestamp, body, now);

        Assert.False(ok);
    }

    [Fact]
    public void Verify_rejects_missing_headers()
    {
        Assert.False(_verifier.Verify("-----BEGIN PUBLIC KEY-----\nX\n-----END PUBLIC KEY-----", null, "123", [], DateTimeOffset.Now));
        Assert.False(_verifier.Verify("-----BEGIN PUBLIC KEY-----\nX\n-----END PUBLIC KEY-----", "sig", null, [], DateTimeOffset.Now));
    }

    private static (string publicPem, string privatePem) GenerateKeyPair()
    {
        // Uses the same Ed25519 API the verifier uses. If the verifier uses
        // BouncyCastle, swap this helper to generate via BouncyCastle.
        using var alg = System.Security.Cryptography.Ed25519.GenerateKey();
        return (alg.ExportSubjectPublicKeyInfoPem(), alg.ExportPkcs8PrivateKeyPem());
    }

    private static string SignForTest(string privatePem, string timestamp, byte[] body)
    {
        var payload = new byte[Encoding.UTF8.GetByteCount(timestamp) + 1 + body.Length];
        var written = Encoding.UTF8.GetBytes(timestamp, payload);
        payload[written] = (byte)'|';
        Buffer.BlockCopy(body, 0, payload, written + 1, body.Length);

        using var alg = System.Security.Cryptography.Ed25519.ImportFromPem(privatePem);
        return Convert.ToBase64String(alg.SignData(payload));
    }
}
```

If `System.Security.Cryptography.Ed25519` is unavailable and you swapped to BouncyCastle in `TelnyxSignatureVerifier`, rewrite `GenerateKeyPair` and `SignForTest` using BouncyCastle equivalents — the assertions remain unchanged.

- [ ] **Step 3: Run the tests — confirm pass**

```bash
dotnet test src/agent/OpenAgent.Tests/OpenAgent.Tests.csproj --filter "FullyQualifiedName~TelnyxSignatureVerifierTests"
```

Expected: 5/5 pass.

- [ ] **Step 4: Commit**

```bash
git add src/agent/OpenAgent.Channel.Telnyx/TelnyxSignatureVerifier.cs \
        src/agent/OpenAgent.Tests/TelnyxSignatureVerifierTests.cs
git commit -m "feat(telnyx): ED25519 webhook signature verifier with replay guard"
```

---

## Task 5: TelnyxMessageHandler — conversation + allowlist + provider call

**Files:**
- Create: `src/agent/OpenAgent.Channel.Telnyx/TelnyxMessageHandler.cs`
- Create: `src/agent/OpenAgent.Tests/TelnyxMessageHandlerTests.cs`
- Create: `src/agent/OpenAgent.Tests/Fakes/FakeTelnyxTextProvider.cs`

- [ ] **Step 1: Inspect how Telegram does it**

Read `src/agent/OpenAgent.Channel.Telegram/TelegramMessageHandler.cs` lines 62–171 to confirm the pattern before writing the Telnyx equivalent. Key methods you'll mirror:
- Allowlist / gate checks
- `_store.FindOrCreateChannelConversation("telegram", connectionId, chatId, …, ConversationType.Text, …)`
- Build user `Message` with role="user", content
- `_textProviderResolver(conversation.Provider).CompleteAsync(conversation, userMessage, ct)` — collect events
- Extract final assistant text from the `TextDelta` events

- [ ] **Step 2: Create the fake text provider**

Create `src/agent/OpenAgent.Tests/Fakes/FakeTelnyxTextProvider.cs`:

```csharp
using OpenAgent.Contracts;
using OpenAgent.Models.Common;
using OpenAgent.Models.Conversations;

namespace OpenAgent.Tests.Fakes;

/// <summary>
/// Returns a single canned reply as TextDelta events. Records the last
/// conversation and user message for assertions.
/// </summary>
public sealed class FakeTelnyxTextProvider : ILlmTextProvider
{
    private readonly string _reply;

    public Conversation? LastConversation { get; private set; }
    public Message? LastUserMessage { get; private set; }

    public FakeTelnyxTextProvider(string reply) { _reply = reply; }

    public async IAsyncEnumerable<CompletionEvent> CompleteAsync(
        Conversation conversation,
        Message userMessage,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        LastConversation = conversation;
        LastUserMessage = userMessage;
        await Task.Yield();
        yield return new TextDelta(_reply);
    }
}
```

If the real interface names differ (e.g. the method is not `CompleteAsync` or `TextDelta` lives elsewhere), adjust this file to match by reading `src/agent/OpenAgent.Contracts/ILlmTextProvider.cs` and `OpenAgent.Models/Common/CompletionEvent.cs`. Follow existing fakes — `FakeTelegramTextProvider.cs` is the closest model.

- [ ] **Step 3: Write the failing handler tests**

Create `src/agent/OpenAgent.Tests/TelnyxMessageHandlerTests.cs`:

```csharp
using Microsoft.Extensions.Logging.Abstractions;
using OpenAgent.Channel.Telnyx;
using OpenAgent.Contracts;
using OpenAgent.Models.Configs;
using OpenAgent.Models.Conversations;
using OpenAgent.Tests.Fakes;
using Xunit;

namespace OpenAgent.Tests;

public class TelnyxMessageHandlerTests
{
    [Fact]
    public async Task Voice_creates_conversation_and_returns_greeting_with_gather()
    {
        var (handler, store, _) = BuildHandler(
            agentName: "OpenAgent",
            options: new TelnyxOptions { BaseUrl = "https://example.com", WebhookId = "abc" });

        var xml = await handler.HandleVoiceAsync(
            connectionId: "conn-1",
            callSid: "call-123",
            from: "+4512345678",
            to: "+4598765432",
            ct: default);

        Assert.Contains("<Gather", xml);
        Assert.Contains("action=\"https://example.com/api/webhook/telnyx/abc/speech\"", xml);
        Assert.NotNull(store.Find("telnyx", "conn-1", "+4512345678"));
    }

    [Fact]
    public async Task Speech_appends_user_message_and_returns_agent_reply()
    {
        var fakeProvider = new FakeTelnyxTextProvider(reply: "The answer is 42.");
        var (handler, store, _) = BuildHandler(
            agentName: "OpenAgent",
            options: new TelnyxOptions { BaseUrl = "https://example.com", WebhookId = "abc" },
            provider: fakeProvider);

        // Seed the conversation by calling voice first.
        await handler.HandleVoiceAsync("conn-1", "call-123", "+4512345678", "+4598765432", default);

        var xml = await handler.HandleSpeechAsync(
            connectionId: "conn-1",
            callSid: "call-123",
            from: "+4512345678",
            speechResult: "What is the answer?",
            ct: default);

        Assert.Contains("<Say>The answer is 42.</Say>", xml);
        Assert.Contains("<Gather", xml);

        Assert.NotNull(fakeProvider.LastConversation);
        Assert.Equal("What is the answer?", fakeProvider.LastUserMessage!.Content);
    }

    [Fact]
    public async Task Voice_rejects_caller_not_on_nonempty_allowlist()
    {
        var options = new TelnyxOptions
        {
            BaseUrl = "https://example.com",
            WebhookId = "abc",
            AllowedNumbers = ["+4599999999"],
        };
        var (handler, store, _) = BuildHandler("OpenAgent", options);

        var xml = await handler.HandleVoiceAsync("conn-1", "call-x", "+4512345678", "+4598765432", default);

        Assert.Contains("<Hangup />", xml);
        Assert.DoesNotContain("<Gather", xml);
        Assert.Null(store.Find("telnyx", "conn-1", "+4512345678"));
    }

    [Fact]
    public async Task Voice_allows_caller_on_allowlist()
    {
        var options = new TelnyxOptions
        {
            BaseUrl = "https://example.com",
            WebhookId = "abc",
            AllowedNumbers = ["+4512345678"],
        };
        var (handler, store, _) = BuildHandler("OpenAgent", options);

        var xml = await handler.HandleVoiceAsync("conn-1", "call-x", "+4512345678", "+4598765432", default);

        Assert.Contains("<Gather", xml);
        Assert.NotNull(store.Find("telnyx", "conn-1", "+4512345678"));
    }

    [Fact]
    public async Task Empty_speech_result_produces_reprompt_not_forwarded_to_provider()
    {
        var fakeProvider = new FakeTelnyxTextProvider(reply: "ignored");
        var (handler, _, _) = BuildHandler(
            "OpenAgent",
            new TelnyxOptions { BaseUrl = "https://example.com", WebhookId = "abc" },
            provider: fakeProvider);

        await handler.HandleVoiceAsync("conn-1", "call-x", "+4512345678", "+4598765432", default);

        var xml = await handler.HandleSpeechAsync("conn-1", "call-x", "+4512345678", speechResult: "", ct: default);

        Assert.Contains("<Gather", xml);
        Assert.Null(fakeProvider.LastUserMessage); // provider was never invoked
    }

    // Helpers —
    private (TelnyxMessageHandler handler, InMemoryConversationStore store, AgentConfig config) BuildHandler(
        string agentName,
        TelnyxOptions options,
        ILlmTextProvider? provider = null)
    {
        var store = new InMemoryConversationStore();
        var agentConfig = new AgentConfig { TextProvider = "fake", TextModel = "fake-1" };
        Func<string, ILlmTextProvider> resolver = _ => provider ?? new FakeTelnyxTextProvider("hello");
        var handler = new TelnyxMessageHandler(
            options,
            "conn-1",
            store,
            resolver,
            agentConfig,
            NullLogger<TelnyxMessageHandler>.Instance);
        return (handler, store, agentConfig);
    }
}
```

If `InMemoryConversationStore` does not already exist under `OpenAgent.Tests`, factor it out from `SqliteConversationStore` usage in existing tests — check `TelegramMessageHandlerTests.cs` and `TelegramWebhookEndpointTests.cs` for how they substitute the store. If they use the real Sqlite store with a temp file, do the same (rename helper accordingly). Do NOT introduce a new abstraction; match what's already in the tests folder.

- [ ] **Step 4: Run the tests — confirm they fail to compile**

Expected: `TelnyxMessageHandler` not found.

```bash
dotnet test src/agent/OpenAgent.Tests/OpenAgent.Tests.csproj --filter "FullyQualifiedName~TelnyxMessageHandlerTests" 2>&1 | tail -10
```

- [ ] **Step 5: Implement TelnyxMessageHandler**

Create `src/agent/OpenAgent.Channel.Telnyx/TelnyxMessageHandler.cs`:

```csharp
using Microsoft.Extensions.Logging;
using OpenAgent.Contracts;
using OpenAgent.Models.Common;
using OpenAgent.Models.Configs;
using OpenAgent.Models.Conversations;

namespace OpenAgent.Channel.Telnyx;

/// <summary>
/// Orchestrates a Telnyx TeXML phone call. Returns TeXML response strings for
/// each webhook turn; persists user/assistant messages to the conversation store.
/// </summary>
public sealed class TelnyxMessageHandler
{
    private readonly TelnyxOptions _options;
    private readonly string _connectionId;
    private readonly IConversationStore _store;
    private readonly Func<string, ILlmTextProvider> _textProviderResolver;
    private readonly AgentConfig _agentConfig;
    private readonly ILogger<TelnyxMessageHandler> _logger;

    private const string ChannelType = "telnyx";
    private const string Source = "telnyx";

    public TelnyxMessageHandler(
        TelnyxOptions options,
        string connectionId,
        IConversationStore store,
        Func<string, ILlmTextProvider> textProviderResolver,
        AgentConfig agentConfig,
        ILogger<TelnyxMessageHandler> logger)
    {
        _options = options;
        _connectionId = connectionId;
        _store = store;
        _textProviderResolver = textProviderResolver;
        _agentConfig = agentConfig;
        _logger = logger;
    }

    public Task<string> HandleVoiceAsync(
        string connectionId,
        string callSid,
        string from,
        string to,
        CancellationToken ct)
    {
        _logger.LogInformation("Telnyx [{ConnectionId}] inbound call {CallSid} from {From} to {To}",
            connectionId, callSid, from, to);

        if (!IsCallerAllowed(from))
        {
            _logger.LogWarning("Telnyx [{ConnectionId}] rejecting caller {From} — not on allowlist", connectionId, from);
            return Task.FromResult(TeXmlBuilder.Reject("Not authorised."));
        }

        _store.FindOrCreateChannelConversation(
            channelType: ChannelType,
            connectionId: connectionId,
            channelChatId: from,
            source: Source,
            type: ConversationType.Phone,
            provider: _agentConfig.TextProvider,
            model: _agentConfig.TextModel);

        var actionUrl = BuildActionUrl("speech");
        return Task.FromResult(TeXmlBuilder.GreetAndGather(
            greeting: "Hi, it's OpenAgent. How can I help you today?",
            gatherActionUrl: actionUrl));
    }

    public async Task<string> HandleSpeechAsync(
        string connectionId,
        string callSid,
        string from,
        string speechResult,
        CancellationToken ct)
    {
        var conversation = _store.FindChannelConversation(ChannelType, connectionId, from);
        if (conversation is null)
        {
            _logger.LogWarning("Telnyx [{ConnectionId}] speech webhook for unknown conversation {From}", connectionId, from);
            return TeXmlBuilder.Farewell("Sorry, something went wrong. Goodbye.");
        }

        if (string.IsNullOrWhiteSpace(speechResult))
        {
            _logger.LogInformation("Telnyx [{ConnectionId}] empty speech result — reprompting", connectionId);
            return TeXmlBuilder.RespondAndGather(
                reply: "I didn't catch that — could you say it again?",
                gatherActionUrl: BuildActionUrl("speech"));
        }

        var userMessage = new Message
        {
            Role = "user",
            Content = speechResult,
        };

        var provider = _textProviderResolver(conversation.Provider);
        var replyText = new System.Text.StringBuilder();
        await foreach (var evt in provider.CompleteAsync(conversation, userMessage, ct))
        {
            if (evt is TextDelta delta)
                replyText.Append(delta.Text);
        }

        var reply = replyText.ToString().Trim();
        if (reply.Length == 0)
        {
            _logger.LogWarning("Telnyx [{ConnectionId}] provider returned empty reply", connectionId);
            return TeXmlBuilder.Farewell("Sorry, I'm having trouble. Goodbye.");
        }

        return TeXmlBuilder.RespondAndGather(reply, BuildActionUrl("speech"));
    }

    public Task HandleHangupAsync(string connectionId, string callSid, string from, CancellationToken ct)
    {
        _logger.LogInformation("Telnyx [{ConnectionId}] call {CallSid} ended (from {From})", connectionId, callSid, from);
        return Task.CompletedTask;
    }

    private bool IsCallerAllowed(string from)
    {
        if (_options.AllowedNumbers.Count == 0) return true;
        return _options.AllowedNumbers.Contains(from);
    }

    private string BuildActionUrl(string suffix)
    {
        var baseUrl = (_options.BaseUrl ?? "").TrimEnd('/');
        var webhookId = _options.WebhookId ?? "_";
        return $"{baseUrl}/api/webhook/telnyx/{webhookId}/{suffix}";
    }
}
```

If `Message` does not have parameterless init (e.g. is a record with required members), adapt to `new Message("user", speechResult)` or whatever the existing shape is — check `src/agent/OpenAgent.Models/` for the real constructor. Keep the assertion semantics identical.

- [ ] **Step 6: Run the tests — confirm pass**

```bash
dotnet test src/agent/OpenAgent.Tests/OpenAgent.Tests.csproj --filter "FullyQualifiedName~TelnyxMessageHandlerTests"
```

Expected: 5/5 pass.

- [ ] **Step 7: Commit**

```bash
git add src/agent/OpenAgent.Channel.Telnyx/TelnyxMessageHandler.cs \
        src/agent/OpenAgent.Tests/TelnyxMessageHandlerTests.cs \
        src/agent/OpenAgent.Tests/Fakes/FakeTelnyxTextProvider.cs
git commit -m "feat(telnyx): message handler for voice/speech/hangup turns"
```

---

## Task 6: Wire TelnyxWebhookEndpoints

**Files:**
- Create: `src/agent/OpenAgent.Channel.Telnyx/TelnyxWebhookEndpoints.cs`
- Modify: `src/agent/OpenAgent.Channel.Telnyx/TelnyxChannelProvider.cs` (expose handler + verifier instances; auto-generate `WebhookId` on start)
- Modify: `src/agent/OpenAgent.Channel.Telnyx/TelnyxChannelProviderFactory.cs` (pass store, text provider resolver, agent config, connection store into provider ctor)
- Modify: `src/agent/OpenAgent/Program.cs` (pass new DI dependencies into factory; call `app.MapTelnyxWebhookEndpoints()`)
- Create: `src/agent/OpenAgent.Tests/TelnyxWebhookEndpointTests.cs`

- [ ] **Step 1: Evolve `TelnyxChannelProvider` to construct handler + verifier + auto-generate WebhookId**

Open `src/agent/OpenAgent.Channel.Telnyx/TelnyxChannelProvider.cs`. Replace with a version that:
1. Takes the additional DI dependencies (store, resolver, agent config, connection store, ILoggerFactory).
2. Constructs a `TelnyxMessageHandler` and a `TelnyxSignatureVerifier` internally.
3. Exposes them via public readonly properties (`Handler`, `SignatureVerifier`).
4. Generates `WebhookId` on first `StartAsync` if null and persists it via `IConnectionStore.Update`.

Follow the shape of `TelegramChannelProvider.cs` where it auto-generates `WebhookId`.

Exact code:

```csharp
using Microsoft.Extensions.Logging;
using OpenAgent.Contracts;
using OpenAgent.Models.Configs;

namespace OpenAgent.Channel.Telnyx;

public sealed class TelnyxChannelProvider : IChannelProvider
{
    private readonly TelnyxOptions _options;
    private readonly string _connectionId;
    private readonly IConnectionStore _connectionStore;
    private readonly ILogger<TelnyxChannelProvider> _logger;

    /// <summary>Strongly-typed configuration for this connection. Exposed for tests that read back factory-parsed values.</summary>
    public TelnyxOptions Options => _options;

    /// <summary>Identifier of the owning connection row.</summary>
    public string ConnectionId => _connectionId;

    /// <summary>Message handler used by the webhook endpoint to process turns.</summary>
    public TelnyxMessageHandler Handler { get; }

    /// <summary>Signature verifier used by the webhook endpoint to validate requests.</summary>
    public TelnyxSignatureVerifier SignatureVerifier { get; }

    /// <summary>The webhook ID embedded in the public URL path. Auto-generated on first start.</summary>
    public string? WebhookId => _options.WebhookId;

    /// <summary>Creates a provider for the given connection. The factory is the only intended caller.</summary>
    public TelnyxChannelProvider(
        TelnyxOptions options,
        string connectionId,
        IConversationStore store,
        IConnectionStore connectionStore,
        Func<string, ILlmTextProvider> textProviderResolver,
        AgentConfig agentConfig,
        ILoggerFactory loggerFactory)
    {
        _options = options;
        _connectionId = connectionId;
        _connectionStore = connectionStore;
        _logger = loggerFactory.CreateLogger<TelnyxChannelProvider>();

        SignatureVerifier = new TelnyxSignatureVerifier(loggerFactory.CreateLogger<TelnyxSignatureVerifier>());
        Handler = new TelnyxMessageHandler(
            options,
            connectionId,
            store,
            textProviderResolver,
            agentConfig,
            loggerFactory.CreateLogger<TelnyxMessageHandler>());
    }

    /// <summary>Starts the Telnyx channel. On first start, auto-generates and persists a WebhookId.</summary>
    public async Task StartAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_options.WebhookId))
        {
            _options.WebhookId = Guid.NewGuid().ToString("N")[..12];
            await PersistWebhookIdAsync(ct);
        }

        _logger.LogInformation(
            "Telnyx [{ConnectionId}] started (phoneNumber={PhoneNumber}, webhookId={WebhookId}, allowedCount={AllowedCount})",
            _connectionId,
            _options.PhoneNumber ?? "<unset>",
            _options.WebhookId,
            _options.AllowedNumbers.Count);
    }

    /// <summary>Stops the Telnyx channel. No-op for TeXML mode — webhooks are stateless.</summary>
    public Task StopAsync(CancellationToken ct)
    {
        _logger.LogInformation("Telnyx [{ConnectionId}] stopped", _connectionId);
        return Task.CompletedTask;
    }

    private async Task PersistWebhookIdAsync(CancellationToken ct)
    {
        var connections = await _connectionStore.ListAsync(ct);
        var connection = connections.FirstOrDefault(c => c.Id == _connectionId);
        if (connection is null) return;

        // Read existing config blob and merge webhookId.
        using var doc = System.Text.Json.JsonDocument.Parse(connection.Config.GetRawText());
        var updated = new System.Collections.Generic.Dictionary<string, object?>();
        foreach (var prop in doc.RootElement.EnumerateObject())
            updated[prop.Name] = System.Text.Json.JsonSerializer.Deserialize<object?>(prop.Value.GetRawText());
        updated["webhookId"] = _options.WebhookId;

        var mergedJson = System.Text.Json.JsonSerializer.Serialize(updated);
        connection.Config = System.Text.Json.JsonDocument.Parse(mergedJson).RootElement.Clone();

        await _connectionStore.UpdateAsync(connection, ct);
    }
}
```

If `IConnectionStore` uses synchronous methods in this repo (check `src/agent/OpenAgent.Contracts/IConnectionStore.cs`), drop the `await` and `ct` parameters accordingly.

- [ ] **Step 2: Update the factory to pass new dependencies**

Open `src/agent/OpenAgent.Channel.Telnyx/TelnyxChannelProviderFactory.cs`. Change the constructor + `Create` to match `TelegramChannelProviderFactory` — take `IConversationStore`, `IConnectionStore`, `Func<string, ILlmTextProvider>`, `AgentConfig`, `ILoggerFactory`.

```csharp
private readonly IConversationStore _store;
private readonly IConnectionStore _connectionStore;
private readonly Func<string, ILlmTextProvider> _textProviderResolver;
private readonly AgentConfig _agentConfig;
private readonly ILoggerFactory _loggerFactory;

public TelnyxChannelProviderFactory(
    IConversationStore store,
    IConnectionStore connectionStore,
    Func<string, ILlmTextProvider> textProviderResolver,
    AgentConfig agentConfig,
    ILoggerFactory loggerFactory)
{
    _store = store;
    _connectionStore = connectionStore;
    _textProviderResolver = textProviderResolver;
    _agentConfig = agentConfig;
    _loggerFactory = loggerFactory;
}

// At the bottom of Create(Connection), replace the `return new TelnyxChannelProvider(...)` line with:
return new TelnyxChannelProvider(
    options,
    connection.Id,
    _store,
    _connectionStore,
    _textProviderResolver,
    _agentConfig,
    _loggerFactory);
```

- [ ] **Step 3: Update existing factory tests to use the new ctor**

The 7 tests in `TelnyxChannelProviderFactoryTests.cs` currently call `new TelnyxChannelProviderFactory(NullLoggerFactory.Instance)`. Update them to pass fakes/stubs for the new dependencies. Introduce a small helper at the top of the test class:

```csharp
private static TelnyxChannelProviderFactory BuildFactory(
    IConversationStore? store = null,
    IConnectionStore? connections = null,
    Func<string, ILlmTextProvider>? resolver = null,
    AgentConfig? config = null) =>
    new(
        store ?? new InMemoryConversationStore(),
        connections ?? new InMemoryConnectionStore(),
        resolver ?? (_ => new FakeTelnyxTextProvider("stub")),
        config ?? new AgentConfig { TextProvider = "fake", TextModel = "fake-1" },
        NullLoggerFactory.Instance);
```

Replace each `new TelnyxChannelProviderFactory(NullLoggerFactory.Instance)` call-site with `BuildFactory()`.

If `InMemoryConnectionStore` doesn't exist, substitute with `new FileConnectionStore(Path.GetTempPath())` or whatever the existing tests use. See `TelegramWebhookEndpointTests.cs` for the idiom.

- [ ] **Step 4: Write `TelnyxWebhookEndpoints`**

Create `src/agent/OpenAgent.Channel.Telnyx/TelnyxWebhookEndpoints.cs`:

```csharp
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenAgent.Contracts;

namespace OpenAgent.Channel.Telnyx;

public static class TelnyxWebhookEndpoints
{
    public static WebApplication MapTelnyxWebhookEndpoints(this WebApplication app)
    {
        app.MapPost("/api/webhook/telnyx/{webhookId}/voice", HandleVoice)
           .AllowAnonymous()
           .WithName("TelnyxVoiceWebhook");

        app.MapPost("/api/webhook/telnyx/{webhookId}/speech", HandleSpeech)
           .AllowAnonymous()
           .WithName("TelnyxSpeechWebhook");

        app.MapPost("/api/webhook/telnyx/{webhookId}/status", HandleStatus)
           .AllowAnonymous()
           .WithName("TelnyxStatusWebhook");

        return app;
    }

    private static async Task<IResult> HandleVoice(
        string webhookId,
        HttpRequest request,
        ConnectionManager connectionManager,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var logger = loggerFactory.CreateLogger("TelnyxWebhook");
        var (provider, rawBody, form) = await ReadAndVerifyAsync(webhookId, request, connectionManager, logger, ct);
        if (provider is null)
            return Results.NotFound();

        var from = form["From"].ToString();
        var to = form["To"].ToString();
        var callSid = form["CallSid"].ToString();

        var xml = await provider.Handler.HandleVoiceAsync(provider.ConnectionId, callSid, from, to, ct);
        return Results.Content(xml, "application/xml");
    }

    private static async Task<IResult> HandleSpeech(
        string webhookId,
        HttpRequest request,
        ConnectionManager connectionManager,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var logger = loggerFactory.CreateLogger("TelnyxWebhook");
        var (provider, rawBody, form) = await ReadAndVerifyAsync(webhookId, request, connectionManager, logger, ct);
        if (provider is null)
            return Results.NotFound();

        var from = form["From"].ToString();
        var callSid = form["CallSid"].ToString();
        var speech = form["SpeechResult"].ToString();

        var xml = await provider.Handler.HandleSpeechAsync(provider.ConnectionId, callSid, from, speech, ct);
        return Results.Content(xml, "application/xml");
    }

    private static async Task<IResult> HandleStatus(
        string webhookId,
        HttpRequest request,
        ConnectionManager connectionManager,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var logger = loggerFactory.CreateLogger("TelnyxWebhook");
        var (provider, _, form) = await ReadAndVerifyAsync(webhookId, request, connectionManager, logger, ct);
        if (provider is null)
            return Results.NotFound();

        var from = form["From"].ToString();
        var callSid = form["CallSid"].ToString();
        var status = form["CallStatus"].ToString();
        if (string.Equals(status, "completed", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase))
        {
            await provider.Handler.HandleHangupAsync(provider.ConnectionId, callSid, from, ct);
        }
        return Results.Ok();
    }

    private static async Task<(TelnyxChannelProvider? provider, byte[] rawBody, IFormCollection form)>
        ReadAndVerifyAsync(
            string webhookId,
            HttpRequest request,
            ConnectionManager connectionManager,
            ILogger logger,
            CancellationToken ct)
    {
        var provider = connectionManager.GetProviders()
            .Select(p => p.Provider)
            .OfType<TelnyxChannelProvider>()
            .FirstOrDefault(p => p.WebhookId == webhookId);

        if (provider is null)
        {
            logger.LogWarning("Telnyx webhook received for unknown webhookId={WebhookId}", webhookId);
            return (null, [], new FormCollection(null));
        }

        using var ms = new MemoryStream();
        await request.Body.CopyToAsync(ms, ct);
        var rawBody = ms.ToArray();

        var sig = request.Headers["Telnyx-Signature-ed25519"].ToString();
        var ts = request.Headers["Telnyx-Timestamp"].ToString();

        if (!provider.SignatureVerifier.Verify(
                provider.Options.WebhookPublicKey,
                sig,
                ts,
                rawBody,
                DateTimeOffset.UtcNow))
        {
            logger.LogWarning("Telnyx webhook for {ConnectionId} failed signature verification", provider.ConnectionId);
            return (null, [], new FormCollection(null));
        }

        // Re-parse form from the buffered body.
        var bodyStream = new MemoryStream(rawBody);
        var reader = new StreamReader(bodyStream);
        var text = await reader.ReadToEndAsync(ct);
        var form = new FormCollection(ParseFormBody(text));

        return (provider, rawBody, form);
    }

    private static Dictionary<string, Microsoft.Extensions.Primitives.StringValues> ParseFormBody(string body)
    {
        var result = new Dictionary<string, Microsoft.Extensions.Primitives.StringValues>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(body)) return result;
        foreach (var pair in body.Split('&'))
        {
            var i = pair.IndexOf('=');
            var key = i < 0 ? Uri.UnescapeDataString(pair) : Uri.UnescapeDataString(pair[..i]);
            var val = i < 0 ? "" : Uri.UnescapeDataString(pair[(i + 1)..].Replace('+', ' '));
            result[key] = val;
        }
        return result;
    }
}
```

If `ConnectionManager` is not directly injectable (i.e. only exposed as `IHostedService`), check how `TelegramWebhookEndpoints` gets the running providers — it may resolve `IEnumerable<IHostedService>` or a dedicated accessor. Mirror whatever works there.

- [ ] **Step 5: Register the endpoints in `Program.cs`**

Open `src/agent/OpenAgent/Program.cs`. Find the existing `app.MapTelegramWebhookEndpoints();` line. Add directly after it:

```csharp
app.MapTelnyxWebhookEndpoints();
```

Update the factory DI registration to pass the new dependencies:

```csharp
builder.Services.AddSingleton<IChannelProviderFactory>(sp =>
    new TelnyxChannelProviderFactory(
        sp.GetRequiredService<IConversationStore>(),
        sp.GetRequiredService<IConnectionStore>(),
        sp.GetRequiredService<Func<string, ILlmTextProvider>>(),
        sp.GetRequiredService<AgentConfig>(),
        sp.GetRequiredService<ILoggerFactory>()));
```

- [ ] **Step 6: Write integration test for the webhook**

Create `src/agent/OpenAgent.Tests/TelnyxWebhookEndpointTests.cs`. Model on `TelegramWebhookEndpointTests.cs`. Minimum coverage:

```csharp
using System.Net;
using System.Net.Http;
using Microsoft.AspNetCore.Mvc.Testing;
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
    public async Task Voice_webhook_for_unknown_webhookId_returns_404()
    {
        var client = _factory.CreateClient();
        var form = new FormUrlEncodedContent(
        [
            new KeyValuePair<string, string>("From", "+4512345678"),
            new KeyValuePair<string, string>("To", "+4598765432"),
            new KeyValuePair<string, string>("CallSid", "call-x"),
        ]);
        var resp = await client.PostAsync("/api/webhook/telnyx/unknown/voice", form);
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task Voice_webhook_for_known_webhookId_returns_TeXML()
    {
        var factory = _factory.WithWebHostBuilder(b => b.ConfigureServices(services =>
        {
            services.AddSingleton<ILlmTextProvider>(new FakeTelnyxTextProvider("ignored-in-voice-handler"));
            services.AddKeyedSingleton<ILlmTextProvider>("fake", (sp, _) => sp.GetRequiredService<ILlmTextProvider>());
        }));

        // Seed a running Telnyx connection with the dev-mode signature bypass (WebhookPublicKey omitted).
        var connectionStore = factory.Services.GetRequiredService<IConnectionStore>();
        var configJson = """
            {
                "apiKey": "KEY",
                "phoneNumber": "+4598765432",
                "baseUrl": "http://localhost",
                "webhookId": "test-webhook-id",
                "allowedNumbers": ""
            }
            """;
        var connection = new Connection
        {
            Id = "telnyx-test-conn",
            Name = "Telnyx Test",
            Type = "telnyx",
            Enabled = true,
            ConversationId = Guid.NewGuid().ToString(),
            Config = JsonDocument.Parse(configJson).RootElement,
        };
        await connectionStore.UpsertAsync(connection, default);

        var connectionManager = factory.Services.GetRequiredService<ConnectionManager>();
        await connectionManager.StartConnectionAsync(connection.Id, default);

        var client = factory.CreateClient();
        var form = new FormUrlEncodedContent(
        [
            new KeyValuePair<string, string>("From", "+4512345678"),
            new KeyValuePair<string, string>("To", "+4598765432"),
            new KeyValuePair<string, string>("CallSid", "call-abc"),
        ]);

        var resp = await client.PostAsync("/api/webhook/telnyx/test-webhook-id/voice", form);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("application/xml", resp.Content.Headers.ContentType?.MediaType);

        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("<Gather", body);
        Assert.Contains("<Say>Hi, it's OpenAgent", body);
    }
}
```

The exact member names on `IConnectionStore` (`UpsertAsync` vs `AddAsync` etc.) and `ConnectionManager` (`StartConnectionAsync` vs `EnsureRunningAsync` etc.) may differ — before implementing, open `TelegramWebhookEndpointTests.cs` and copy its seeding idiom verbatim. If it uses sync methods, drop the `await` and `default` cancellation tokens here too. Keep the assertions (`HttpStatusCode.OK`, `<Gather`, `<Say>Hi, it's OpenAgent`) exactly as written — they are the contract.

- [ ] **Step 7: Run the full test suite**

```bash
dotnet test
```

Expected: all tests pass (existing + new). If the factory test changes broke other files, fix call-sites to use the `BuildFactory(...)` helper.

- [ ] **Step 8: Commit**

```bash
git add src/agent/OpenAgent.Channel.Telnyx/TelnyxWebhookEndpoints.cs \
        src/agent/OpenAgent.Channel.Telnyx/TelnyxChannelProvider.cs \
        src/agent/OpenAgent.Channel.Telnyx/TelnyxChannelProviderFactory.cs \
        src/agent/OpenAgent.Tests/TelnyxChannelProviderFactoryTests.cs \
        src/agent/OpenAgent.Tests/TelnyxWebhookEndpointTests.cs \
        src/agent/OpenAgent/Program.cs
git commit -m "feat(telnyx): webhook endpoints for voice, speech, status"
```

---

## Task 7: End-to-end verification with Telnyx portal

**Files:** none modified — verification only.

- [ ] **Step 1: Prerequisites**

You need:
- A Telnyx account with API key (free trial is fine)
- A purchased Telnyx phone number (free in trial)
- A TeXML Application configured in the Telnyx portal
- A public HTTPS URL for your local dev instance — install and run `ngrok http 5264` (or `cloudflared tunnel`)
- The dev server running locally on port 5264

- [ ] **Step 2: Configure Telnyx TeXML Application**

In the Telnyx portal:
1. Create a TeXML Application
2. Set `Webhook URL` = `https://<your-ngrok-subdomain>.ngrok.io/api/webhook/telnyx/<webhookId>/voice`
3. Set `Status Callback URL` = `https://<your-ngrok-subdomain>.ngrok.io/api/webhook/telnyx/<webhookId>/status`
4. Copy the `Outbound Voice Profile`'s ED25519 public key
5. Assign the TeXML Application to your Telnyx phone number

`<webhookId>` is the 12-character GUID auto-generated on first start of the Telnyx connection — read it from the server log: `Telnyx [...] started (... webhookId=<value> ...)`.

- [ ] **Step 3: Save config in OpenAgent**

Via the Settings UI or `PUT /api/connections/{id}`:
- `apiKey`: Telnyx API key
- `phoneNumber`: your E.164 number (e.g. `+4512345678`)
- `baseUrl`: `https://<your-ngrok-subdomain>.ngrok.io`
- `webhookPublicKey`: the ED25519 PEM from the portal
- `webhookSecret`: (leave blank — not used in TeXML mode)
- `allowedNumbers`: your own mobile number (comma-separated if more)

Enable and start the connection.

- [ ] **Step 4: Place the call**

Dial the Telnyx number from your mobile. Expected flow:
1. You hear: "Hi, it's OpenAgent. How can I help you today?"
2. Say something: "What's the date today?"
3. You hear the agent's reply
4. Say "goodbye"
5. Hang up

- [ ] **Step 5: Verify persistence**

- Check server logs for `Telnyx [...] inbound call <callSid> from +... to +...`
- Check the Conversations UI for a new conversation with `source=telnyx`, `type=Phone`, `displayName=+4512345678`
- Messages in the conversation should include the transcribed user speech and the agent reply

No commit for this task.

---

## Done criteria

- [ ] `ConversationType.Phone` exists and is persisted via SQLite migration path
- [ ] `PHONE.md` is extracted on first run and included in the system prompt for Phone conversations
- [ ] `TelnyxOptions` carries `WebhookId`, `WebhookPublicKey`, `BaseUrl`; factory parses them from the JsonElement
- [ ] `WebhookId` is auto-generated on first start and persisted back to `connections.json`
- [ ] `TelnyxSignatureVerifier` validates ED25519 signatures over `{timestamp}|{body}` with 300s replay window; skips when key absent
- [ ] `TeXmlBuilder` produces greeting/reply/farewell/reject XML with proper escaping
- [ ] `TelnyxMessageHandler` creates Phone conversations, enforces allowlist, calls text provider, returns TeXML
- [ ] `TelnyxWebhookEndpoints` registered at `/api/webhook/telnyx/{webhookId}/voice`, `/speech`, `/status` — all `AllowAnonymous`
- [ ] Full test suite passes (existing + 20+ new)
- [ ] End-to-end call verified: you can call the Telnyx number and have a turn-based conversation with the agent

Plan 3 (Media Streaming + Realtime bridge) replaces the TeXML `<Gather input="speech">` loop with a WebSocket-based audio pipe to `ILlmVoiceProvider`, gaining barge-in and low latency.
