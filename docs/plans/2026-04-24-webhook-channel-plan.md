# Webhook Channel Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a single anonymous HTTP endpoint that pushes a plain-text body into a named conversation as a user message and kicks off LLM completion in the background.

**Architecture:** One new endpoint file (`WebhookEndpoints.cs`) following the existing extension-method pattern (`MapConversationEndpoints`, `MapChatEndpoints`). Uses `IConversationStore.Get` (not `GetOrCreate`) to enforce "must exist". Resolves `ILlmTextProvider` via keyed DI on `conversation.Provider`. Fires `Task.Run` and returns 202 without awaiting. No new contracts, no `IChannelProvider`, no `Connection` row.

**Tech Stack:** ASP.NET Core Minimal APIs (`WebApplication`), xUnit + `WebApplicationFactory<Program>` for integration tests. Target framework: net10.0.

**Spec:** `docs/plans/2026-04-24-webhook-channel-design.md`

---

## File Structure

**Create:**
- `src/agent/OpenAgent.Api/Endpoints/WebhookEndpoints.cs` — the new endpoint (~60 lines).
- `src/agent/OpenAgent.Tests/WebhookEndpointTests.cs` — integration tests.

**Modify:**
- `src/agent/OpenAgent/Program.cs` — add `app.MapWebhookEndpoints();` line (one line).
- `CLAUDE.md` — add `#### Webhooks` subsection under the API Reference block.

**No changes to:** `OpenAgent.Contracts`, `OpenAgent.Models`, `OpenAgent.ConversationStore.Sqlite`, any `OpenAgent.Channel.*` project, any `OpenAgent.LlmText.*` project, the React frontend.

---

## Task 1: Empty-body validation (proves route exists)

Establishes the route, the skeleton, and the `400` check in one bite. Done first because an empty-body `400` is the only way to distinguish "route is mapped" from "route not found" (both of which would otherwise return `404`).

**Files:**
- Create: `src/agent/OpenAgent.Api/Endpoints/WebhookEndpoints.cs`
- Create: `src/agent/OpenAgent.Tests/WebhookEndpointTests.cs`
- Modify: `src/agent/OpenAgent/Program.cs` — add the mapper call

- [ ] **Step 1: Write the failing test**

Create `src/agent/OpenAgent.Tests/WebhookEndpointTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OpenAgent.Contracts;
using OpenAgent.Models.Common;
using OpenAgent.Models.Configs;
using OpenAgent.Models.Conversations;
using OpenAgent.Models.Providers;

namespace OpenAgent.Tests;

public class WebhookEndpointTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly CapturingTextProvider _capturingProvider;

    public WebhookEndpointTests(WebApplicationFactory<Program> factory)
    {
        TestSetup.EnsureConfigSeeded();
        _capturingProvider = new CapturingTextProvider();
        _factory = factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                services.RemoveAll(typeof(ILlmTextProvider));
                services.AddKeyedSingleton<ILlmTextProvider>("azure-openai-text", _capturingProvider);
                services.AddSingleton<ILlmTextProvider>(_capturingProvider);
            });
        });
    }

    [Fact]
    public async Task PostWebhook_EmptyBody_Returns400()
    {
        // Pre-create a conversation so we're sure 400 is about the body, not the conversation
        var store = _factory.Services.GetRequiredService<IConversationStore>();
        var conversationId = Guid.NewGuid().ToString();
        store.GetOrCreate(conversationId, "app", ConversationType.Text, "azure-openai-text", "test-model");

        var client = _factory.CreateClient();
        var response = await client.PostAsync(
            $"/api/webhook/conversation/{conversationId}",
            new StringContent("", Encoding.UTF8, "text/plain"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private sealed class CapturingTextProvider : ILlmTextProvider
    {
        public Conversation? LastConversation { get; private set; }
        public Message? LastUserMessage { get; private set; }
        public int CallCount { get; private set; }

        public string Key => "text-provider";
        public IReadOnlyList<ProviderConfigField> ConfigFields => [];
        public void Configure(JsonElement configuration) { }
        public int? GetContextWindow(string model) => null;

        public async IAsyncEnumerable<CompletionEvent> CompleteAsync(Conversation conversation, Message userMessage,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            LastConversation = conversation;
            LastUserMessage = userMessage;
            CallCount++;
            yield return new TextDelta("ok");
            await Task.CompletedTask;
        }

        public async IAsyncEnumerable<CompletionEvent> CompleteAsync(IReadOnlyList<Message> messages, string model,
            CompletionOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            yield return new TextDelta("raw");
            await Task.CompletedTask;
        }
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

From `src/agent`:

```
dotnet test OpenAgent.Tests --filter "FullyQualifiedName~WebhookEndpointTests.PostWebhook_EmptyBody_Returns400"
```

Expected: FAIL. The endpoint doesn't exist yet, so ASP.NET returns `404 Not Found` (route not matched), not `400`. Assertion failure should read "Expected BadRequest but was NotFound" or similar.

- [ ] **Step 3: Create the endpoint file**

Create `src/agent/OpenAgent.Api/Endpoints/WebhookEndpoints.cs`:

```csharp
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenAgent.Contracts;
using OpenAgent.Models.Common;
using OpenAgent.Models.Conversations;

namespace OpenAgent.Api.Endpoints;

/// <summary>
/// Anonymous webhook endpoint for pushing events into existing conversations.
/// The conversation GUID in the URL is the capability — unguessable by design.
/// No auto-create (404 if missing), no sync wait (202 returned immediately).
/// </summary>
public static class WebhookEndpoints
{
    /// <summary>
    /// Maps POST /api/webhook/conversation/{conversationId} for pushing a plain-text
    /// body as a user message into an existing conversation.
    /// </summary>
    public static void MapWebhookEndpoints(this WebApplication app)
    {
        app.MapPost("/api/webhook/conversation/{conversationId}", async (
            string conversationId,
            HttpRequest request,
            IConversationStore store,
            IServiceProvider services,
            ILoggerFactory loggerFactory,
            CancellationToken ct) =>
        {
            // Read raw body as UTF-8 text — Content-Type is not validated
            using var reader = new StreamReader(request.Body);
            var body = await reader.ReadToEndAsync(ct);

            if (string.IsNullOrWhiteSpace(body))
                return Results.BadRequest(new { error = "body is empty" });

            return Results.Accepted();
        }).AllowAnonymous();
    }
}
```

- [ ] **Step 4: Wire the mapper into `Program.cs`**

Open `src/agent/OpenAgent/Program.cs`. Find the block of `app.Map*Endpoints()` calls (around line 262-276). Add the webhook mapper after `MapTelegramWebhookEndpoints` (keep webhook-shaped endpoints grouped):

Find:
```csharp
app.MapTelegramWebhookEndpoints();
app.MapWhatsAppEndpoints();
```

Replace with:
```csharp
app.MapTelegramWebhookEndpoints();
app.MapWebhookEndpoints();
app.MapWhatsAppEndpoints();
```

- [ ] **Step 5: Run test to verify it passes**

```
dotnet test OpenAgent.Tests --filter "FullyQualifiedName~WebhookEndpointTests.PostWebhook_EmptyBody_Returns400"
```

Expected: PASS.

- [ ] **Step 6: Commit**

```
git add src/agent/OpenAgent.Api/Endpoints/WebhookEndpoints.cs src/agent/OpenAgent.Tests/WebhookEndpointTests.cs src/agent/OpenAgent/Program.cs
git commit -m "feat(webhook): scaffold /api/webhook/conversation/{id} with empty-body 400"
```

---

## Task 2: 404 for unknown conversation

**Files:**
- Modify: `src/agent/OpenAgent.Api/Endpoints/WebhookEndpoints.cs`
- Modify: `src/agent/OpenAgent.Tests/WebhookEndpointTests.cs`

- [ ] **Step 1: Add the failing test**

Append this test method to `WebhookEndpointTests` class (just below `PostWebhook_EmptyBody_Returns400`):

```csharp
[Fact]
public async Task PostWebhook_UnknownConversationId_Returns404AndDoesNotCreate()
{
    var client = _factory.CreateClient();
    var unknownId = Guid.NewGuid().ToString();

    var response = await client.PostAsync(
        $"/api/webhook/conversation/{unknownId}",
        new StringContent("some event", Encoding.UTF8, "text/plain"));

    Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

    // Verify auto-create did NOT happen
    var store = _factory.Services.GetRequiredService<IConversationStore>();
    var conv = store.Get(unknownId);
    Assert.Null(conv);
}
```

- [ ] **Step 2: Run test to verify it fails**

```
dotnet test OpenAgent.Tests --filter "FullyQualifiedName~WebhookEndpointTests.PostWebhook_UnknownConversationId_Returns404AndDoesNotCreate"
```

Expected: FAIL with "Expected NotFound but was Accepted" (the current handler returns 202 for any non-empty body).

- [ ] **Step 3: Add the lookup to the handler**

In `WebhookEndpoints.cs`, replace the handler body. Find:

```csharp
            if (string.IsNullOrWhiteSpace(body))
                return Results.BadRequest(new { error = "body is empty" });

            return Results.Accepted();
```

Replace with:

```csharp
            if (string.IsNullOrWhiteSpace(body))
                return Results.BadRequest(new { error = "body is empty" });

            var conversation = store.Get(conversationId);
            if (conversation is null)
                return Results.NotFound();

            return Results.Accepted();
```

- [ ] **Step 4: Run both tests**

```
dotnet test OpenAgent.Tests --filter "FullyQualifiedName~WebhookEndpointTests"
```

Expected: 2 PASS (empty-body 400, unknown-conversation 404).

- [ ] **Step 5: Commit**

```
git add src/agent/OpenAgent.Api/Endpoints/WebhookEndpoints.cs src/agent/OpenAgent.Tests/WebhookEndpointTests.cs
git commit -m "feat(webhook): return 404 when conversation does not exist"
```

---

## Task 3: Happy path — 202 + completion kicked off

**Files:**
- Modify: `src/agent/OpenAgent.Api/Endpoints/WebhookEndpoints.cs`
- Modify: `src/agent/OpenAgent.Tests/WebhookEndpointTests.cs`

- [ ] **Step 1: Add the failing test**

Append this test method to `WebhookEndpointTests`:

```csharp
[Fact]
public async Task PostWebhook_ValidBody_Returns202AndTriggersCompletion()
{
    var store = _factory.Services.GetRequiredService<IConversationStore>();
    var conversationId = Guid.NewGuid().ToString();
    store.GetOrCreate(conversationId, "app", ConversationType.Text, "azure-openai-text", "test-model");

    var client = _factory.CreateClient();
    var response = await client.PostAsync(
        $"/api/webhook/conversation/{conversationId}",
        new StringContent("new episode added: Foo S01E02", Encoding.UTF8, "text/plain"));

    Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

    // Fire-and-forget: poll briefly until the background task captures the call
    var deadline = DateTime.UtcNow.AddSeconds(2);
    while (_capturingProvider.CallCount == 0 && DateTime.UtcNow < deadline)
        await Task.Delay(25);

    Assert.Equal(1, _capturingProvider.CallCount);
    Assert.NotNull(_capturingProvider.LastConversation);
    Assert.Equal(conversationId, _capturingProvider.LastConversation!.Id);
    Assert.NotNull(_capturingProvider.LastUserMessage);
    Assert.Equal("user", _capturingProvider.LastUserMessage!.Role);
    Assert.Equal("new episode added: Foo S01E02", _capturingProvider.LastUserMessage!.Content);
}
```

- [ ] **Step 2: Run test to verify it fails**

```
dotnet test OpenAgent.Tests --filter "FullyQualifiedName~WebhookEndpointTests.PostWebhook_ValidBody_Returns202AndTriggersCompletion"
```

Expected: FAIL — the handler returns 202 but never invokes the provider, so `CallCount` stays 0. Assertion failure at `Assert.Equal(1, _capturingProvider.CallCount)`.

- [ ] **Step 3: Wire the provider call into the handler**

In `WebhookEndpoints.cs`, replace the handler body. Find:

```csharp
            var conversation = store.Get(conversationId);
            if (conversation is null)
                return Results.NotFound();

            return Results.Accepted();
```

Replace with:

```csharp
            var conversation = store.Get(conversationId);
            if (conversation is null)
                return Results.NotFound();

            var userMessage = new Message
            {
                Id = Guid.NewGuid().ToString(),
                ConversationId = conversationId,
                Role = "user",
                Content = body,
                Modality = MessageModality.Text
            };

            var textProvider = services.GetRequiredKeyedService<ILlmTextProvider>(conversation.Provider);
            var logger = loggerFactory.CreateLogger("WebhookEndpoints");

            // Fire-and-forget — intentionally use CancellationToken.None so the completion
            // survives the HTTP response being sent. Safe because IConversationStore and
            // the provider are singletons, not request-scoped.
            _ = Task.Run(async () =>
            {
                try
                {
                    await foreach (var _ in textProvider.CompleteAsync(conversation, userMessage, CancellationToken.None))
                    {
                    }
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Webhook completion failed for conversation {ConversationId}", conversationId);
                }
            });

            return Results.Accepted();
```

- [ ] **Step 4: Run all webhook tests**

```
dotnet test OpenAgent.Tests --filter "FullyQualifiedName~WebhookEndpointTests"
```

Expected: 3 PASS.

- [ ] **Step 5: Commit**

```
git add src/agent/OpenAgent.Api/Endpoints/WebhookEndpoints.cs src/agent/OpenAgent.Tests/WebhookEndpointTests.cs
git commit -m "feat(webhook): push body as user message and trigger completion in background"
```

---

## Task 4: Documentation

**Files:**
- Modify: `CLAUDE.md`

- [ ] **Step 1: Add the Webhooks subsection**

In `CLAUDE.md`, find this block (around line 191):

```markdown
| `POST` | `/api/connections/{connectionId}/stop` | Stop connection |

### Authentication
```

Replace with:

```markdown
| `POST` | `/api/connections/{connectionId}/stop` | Stop connection |

#### Webhooks
| Method | Route | Description |
|--------|-------|-------------|
| `POST` | `/api/webhook/conversation/{conversationId}` | Anonymous. Push body (plain text, any `Content-Type`) as a user message into an existing conversation; agent processes asynchronously. Returns `202`. `404` if conversation does not exist, `400` if body empty. |

### Authentication
```

- [ ] **Step 2: Commit**

```
git add CLAUDE.md
git commit -m "docs(webhook): add /api/webhook/conversation to API reference"
```

---

## Task 5: Final verification

- [ ] **Step 1: Full build**

From `src/agent`:

```
dotnet build
```

Expected: build succeeds, 0 errors, 0 warnings (beyond any pre-existing ones).

- [ ] **Step 2: Full test suite**

```
dotnet test
```

Expected: all tests pass, including the 3 new `WebhookEndpointTests`.

- [ ] **Step 3: Manual smoke (optional but recommended)**

Start the agent (`dotnet run --project OpenAgent`) and from another shell:

```
# Create a conversation via the regular API (grab X-Api-Key from console output)
curl -sX POST http://localhost:8080/api/conversations/ -H "X-Api-Key: <key>"
# Use the returned id to fire a webhook
curl -i -X POST http://localhost:8080/api/webhook/conversation/<id> \
  -H "Content-Type: text/plain" \
  -d "Sonarr says: new episode of Foo available"
```

Expected: first curl returns `{"id":"..."}`. Second curl returns `HTTP/1.1 202 Accepted` and the event lands as a user message in that conversation (visible in the React UI).

- [ ] **Step 4: Push**

```
git push
```

---

## Spec Coverage Check

| Spec requirement | Task |
|------------------|------|
| `POST /api/webhook/conversation/{conversationId}` route | Task 1 |
| `AllowAnonymous` | Task 1 (`.AllowAnonymous()` in endpoint) |
| Content-Type not validated; raw UTF-8 body | Task 1 (`StreamReader(request.Body)`) |
| Body used verbatim as user-message content | Task 3 |
| `202 Accepted` on happy path | Task 3 |
| `404` for unknown conversationId | Task 2 |
| `400` for empty body | Task 1 |
| No auto-create | Task 2 (`store.Get`, not `GetOrCreate`; test asserts absence) |
| Fire-and-forget completion via `Task.Run` | Task 3 |
| Completion uses `CancellationToken.None` | Task 3 |
| Exceptions logged via `ILogger` | Task 3 |
| Provider resolved via keyed singleton | Task 3 (`GetRequiredKeyedService<ILlmTextProvider>(conversation.Provider)`) |
| No new contracts / channel provider / Connection row | All tasks — no files touched in those projects |
| Tests: existing + valid, unknown, empty-body | Tasks 1, 2, 3 |
| CLAUDE.md Webhooks subsection | Task 4 |
