# Conversation Guide Endpoint — Design

**Date:** 2026-06-02
**Status:** Approved, ready for implementation plan

## Problem

While working in Claude Code, I want to hand work off to my OpenAgent — e.g. "log 2 hours on the Trison project" should reach my time/finance agent, which logs it, or asks a clarifying question back. Claude Code then either answers the question from repo/session context or relays it to me, and replies again. A natural agent-to-agent loop, with Claude Code as the client in the middle.

The transport for this already exists. What's missing is **discovery**: nothing tells Claude Code what a given conversation is *for* or how to talk to it.

## What already exists (no changes needed)

A message can be POSTed today and the agent's response comes back in the same HTTP call:

- **`POST /api/conversations/{conversationId}/messages`** — sends a user message, runs the full turn (including tool calls and the multi-round loop), and returns a JSON array of completion events. The `{"type":"text","content":"..."}` events concatenated together *are* the agent's reply.
- **`GET /api/conversations/{conversationId}/messages`** — conversation history.
- **`PATCH /api/conversations/{conversationId}`** — updates writable fields including `intention`.
- **`set_intention` tool** — lets the agent author its own intention from any channel.

Auth is `X-Api-Key` on all of these. So Claude Code can already drive a conversation end-to-end. It just can't *discover* one.

## The one new piece: `GET /api/conversations/{conversationId}/guide`

A self-describing endpoint. The conversation's existing `Intention` field becomes the "skill" — a server-authored description of what this agent does — and the endpoint pairs it with static instructions for how to post a message. Claude reads the guide, then talks to the conversation via the existing POST.

### Behavior

- **404 if the conversation does not exist.** No auto-create. The conversation must be created and its intention seeded first (one-time setup). Mirrors the existing `store.Get(...) is null → Results.NotFound()` pattern.
- **200 otherwise**, returning intention + usage.

### Response shape

```json
{
  "conversation_id": "claude-code-finance",
  "intention": "I log time and handle finance via Paymo. Tell me hours, project, and date. I'll ask if anything is ambiguous.",
  "usage": {
    "method": "POST",
    "url": "/api/conversations/claude-code-finance/messages",
    "auth": "X-Api-Key header",
    "body": { "content": "your message to the agent" },
    "response": "JSON array of completion events; concatenate the text of every {\"type\":\"text\"} event to get the agent's reply"
  }
}
```

`intention` is dynamic (read from the conversation). `usage` is constant — the same posting instructions for every conversation. If `intention` is empty/null, return it as `null` (or empty string) — the conversation exists but hasn't been given a purpose yet.

### Placement

Add to the `/api/conversations` route group in `ConversationEndpoints.cs`, next to the other `GET /{conversationId}/...` routes. Requires authorization (inherits from the group). Snake-case JSON to match the rest of the API.

## Client side (Claude Code)

A single pointer in the dev-machine `CLAUDE.md`:

> Your time/finance agent lives at `GET {baseUrl}/api/conversations/claude-code-finance/guide`. Call it to learn what the agent does and how to reach it, then POST messages as the guide describes. If the agent asks a question, answer it from context or relay it to me.

No skill file, no MCP server, no wrapper script. The guide is self-documenting, so the client needs to know only the URL.

## The interaction loop

1. Claude Code GETs the guide → learns the intention + how to post.
2. Claude POSTs `{"content": "log 2h on Trison today"}` to `/messages`.
3. Response events come back; Claude concatenates the `text` events into the reply.
4. If the reply is a clarifying question, Claude answers it (from repo/session context) and POSTs again, or surfaces it to the user.
5. Because the conversation ID is stable, the finance agent keeps continuity across sessions.

## Setup (one-time)

1. Create the conversation: `POST /api/conversations` (or any first POST to `/messages` with a chosen ID — but since the app's create returns a GUID, prefer a stable human ID like `claude-code-finance` by POSTing a first message to it, or PATCH after creation).
2. Set its intention: `PATCH /api/conversations/claude-code-finance` with `{"intention": "..."}`, or let the agent set it via `set_intention`.
3. Optionally activate the relevant skill (e.g. paymo) on that conversation so it behaves as the finance agent.

## Deliberately out of scope (YAGNI)

- **`POST /ask` clean-reply endpoint.** The existing POST already returns everything; concatenating text events is trivial for the client. Add only if event-array handling proves annoying in practice.
- **Agent directory endpoint.** Claude knows the one conversation ID. Add a listing endpoint only when there are multiple exposed agents (finance, ops, …).
- **`needs_input` / "agent is waiting" flag.** Claude judges from the reply text like a human would.
- **Streaming.** Claude Code wants one answer, not a token stream. The WebSocket path stays the app's concern.

## Testing

- `GET /guide` on a non-existent conversation → `404`.
- `GET /guide` on an existing conversation with an intention → `200`, body contains that intention and the constant `usage` block.
- `GET /guide` on an existing conversation with no intention → `200`, `intention` null/empty.
- Auth: request without `X-Api-Key` → unauthorized.
