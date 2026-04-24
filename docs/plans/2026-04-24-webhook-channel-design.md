# Webhook Channel Design

**Issue:** [#13 Webhook channel](https://github.com/users/mbundgaard/projects/4)
**Date:** 2026-04-24
**Status:** Approved

## Goal

Let external systems push events into the agent by addressing a specific conversation directly via HTTP. Primary use case: a media library (Sonarr / Radarr / Plex) notifying the agent when something is added, so the agent can react — e.g. relay a heads-up to the user via a Telegram conversation it shares.

## Non-Goals

- No synchronous request/response. Callers do not wait for the agent's reply.
- No new channel provider, no `Connection` row, no settings UI, no QR-style setup flow.
- No body wrapping ceremony (`<webhook_event>...</webhook_event>` etc.). The body is the user-message content as-is.
- No "default" / "main" conversation fallback. Every request names a conversation explicitly.
- No body-format negotiation. Plain text in, period.

## Endpoint

```
POST /api/webhook/conversation/{conversationId}
Content-Type: text/plain
Body: <free-form text>
```

- `conversationId` is the OpenAgent conversation GUID. Required path segment, no default.
- `AllowAnonymous`. The GUID is the capability — its unguessability is the entire auth story for v1.
- `Content-Type` is **not** validated. The endpoint reads the raw request body as UTF-8 text and uses it verbatim. Callers passing JSON simply get the JSON-as-text into the conversation; the LLM handles it.
- Response codes:
  - **202 Accepted** — body accepted, completion kicked off in the background.
  - **404 Not Found** — no conversation with that ID exists. Webhook does **not** auto-create.
  - **400 Bad Request** — body is empty or whitespace-only.

## Threat Model

- Self-hosted personal agent. "External" callers are services in the same trust boundary (homelab Sonarr, n8n on the same network).
- GUIDs are 122 bits of entropy — unguessable in practice.
- An X-Api-Key holder authorises which conversations are webhook-reachable by creating them in the first place. The webhook endpoint cannot create new conversations; it can only post to existing ones. This is the wedge that distinguishes "admin" from "event-pusher".
- Out of scope for v1: per-conversation secret rotation, HMAC body signing, public exposure hardening. Add later if needed (a reverse proxy can also rewrite headers without app changes).

## Data Flow

1. Request arrives with `conversationId` in the URL and a plain-text body.
2. Validate body is non-empty → else `400`.
3. `IConversationStore.Get(conversationId)` → if `null`, return `404`.
4. Build `Message { Id=Guid.NewGuid(), ConversationId, Role="user", Content=body, Modality=Text }`.
5. Resolve the text provider via `services.GetRequiredKeyedService<ILlmTextProvider>(conversation.Provider)`.
6. Kick off completion in a fire-and-forget `Task.Run`:
   ```csharp
   _ = Task.Run(async () =>
   {
       try
       {
           await foreach (var _ in textProvider.CompleteAsync(conversation, userMessage, CancellationToken.None)) { }
       }
       catch (Exception ex)
       {
           logger.LogError(ex, "Webhook completion failed for conversation {ConversationId}", conversationId);
       }
   });
   ```
7. Return `202 Accepted` immediately. No body.

The provider is responsible for persisting the user message, the assistant reply, and any tool-call rounds — the same path every other channel uses. We do not duplicate that logic.

## Outbound

The webhook endpoint has no outbound side. Whatever the agent says lands in the conversation, period. If the same conversation is also bound to a Telegram (or WhatsApp, etc.) chat that has an `IOutboundSender`, the agent's reply naturally flows out via that channel — no new glue needed.

For pure webhook-only conversations, the reply just sits in conversation history, viewable in the React UI. The agent can also call any of its existing tools (e.g. a future `send_telegram` tool) to push notifications elsewhere.

## DI Scoping Note

`IConversationStore` (SqliteConversationStore) is registered as a singleton.
`ILlmTextProvider` implementations are registered as keyed singletons.

Neither depends on a request-scoped lifetime, so the fire-and-forget `Task.Run` is safe — the request scope can dispose without killing the background work. This mirrors the pattern already used by `TelegramWebhookEndpoints.MapTelegramWebhookEndpoints`.

We pass `CancellationToken.None` to the background task (not `HttpContext.RequestAborted`), so the completion does not abort when the HTTP response returns.

## Code Footprint

- **New file:** `src/agent/OpenAgent.Api/Endpoints/WebhookEndpoints.cs` (~50 lines).
- **One line added to `Program.cs`:** `app.MapWebhookEndpoints();` near the other endpoint mappers.
- **No changes** to `OpenAgent.Contracts`, `OpenAgent.Models`, `OpenAgent.ConversationStore.Sqlite`, or any provider/channel project.

## Tests (`OpenAgent.Tests`)

New test class `WebhookEndpointTests` using the existing `WebApplicationFactory` fixture pattern. Copy the `FakeTextProvider` setup from `ChatEndpointTests.cs` (line 103) — it registers as a keyed singleton and produces deterministic completion events.

1. **Existing conversation, valid body** — first POST a regular chat message via `/api/conversations/{id}/messages` to materialise the conversation, then POST plain text to the webhook URL. Assert `202`. Reload conversation messages via `GET /api/conversations/{id}` and assert the body appears as a `user` role message and the LLM ran (assistant message present). Because completion is fire-and-forget, the test must poll/wait for the assistant message to appear (use a short timeout, e.g. 2s, then fail).
2. **Unknown conversationId** — POST to a random GUID. Assert `404`. Assert via `GET /api/conversations` that no conversation with that GUID was created (auto-create is explicitly off).
3. **Empty body** — POST with no body / whitespace-only body to a valid conversation. Assert `400`. Assert no message was persisted.

## Documentation Updates

`CLAUDE.md` — add a new section under the "API Reference" block:

```
#### Webhooks
| Method | Route | Description |
|--------|-------|-------------|
| `POST` | `/api/webhook/conversation/{conversationId}` | Anonymous. Push body as user message into existing conversation; agent processes asynchronously. 404 if conversation does not exist. |
```

No README update needed — this is API surface, not user-facing setup.

## Open Questions

None. All design decisions resolved during brainstorming:
- URL shape: `/api/webhook/conversation/{conversationId}`.
- Conversation must exist (no auto-create).
- Auth: anonymous, GUID as capability.
- Response: `202` fire-and-forget.
- Body: plain text, used verbatim as user message content.
