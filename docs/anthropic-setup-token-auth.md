# Anthropic Setup-Token Authentication

How to use your Claude subscription (Pro, Team, Enterprise) to call the Anthropic Messages API from your own application, without separate API billing.

## What is a setup-token?

A setup-token is an OAuth access token issued by the Claude Code CLI. It starts with `sk-ant-oat01-` and is typically 80+ characters long. It uses your Claude plan's included usage.

Setup-tokens are **not** the same as API keys (`sk-ant-api-...`). API keys are billed per-token via the Anthropic Console. Setup-tokens use your subscription.

## Getting a token

```bash
claude setup-token
```

Run this on any machine where Claude Code is installed and signed in. The token can be used on a different machine.

## Token lifecycle

- **No auto-refresh.** Static bearer token. When it expires or is revoked, generate a new one.
- **Server-side revocation** happens when you sign out of Claude Code, change your password, or Anthropic rotates credentials.
- **Concurrent sessions** may cause 429 rate limit errors. If another Claude Code session is active on the same account, your requests may be rejected.

## Implementation guide

The standard Anthropic API rejects OAuth tokens with "OAuth authentication is currently not supported". To use a setup-token, your requests must impersonate the Claude Code CLI.

### Endpoint

```
POST https://api.anthropic.com/v1/messages
```

Same endpoint as API key requests.

### Required headers

| Header | Value | Notes |
|--------|-------|-------|
| `Authorization` | `Bearer <setup-token>` | **Must be set per-request**, not as a default header on HttpClient |
| `anthropic-version` | `2023-06-01` | Same as API key requests |
| `anthropic-beta` | `claude-code-20250219,oauth-2025-04-20,fine-grained-tool-streaming-2025-05-14` | All three required. Order: claude-code first |
| `x-app` | `cli` | Identity header |
| `user-agent` | `claude-cli/<version>` | Use a real Claude Code version (e.g. `claude-cli/2.1.91`) |
| `anthropic-dangerous-direct-browser-access` | `true` | Required for OAuth path |
| `accept` | `application/json` | Explicit accept header |

### Critical: per-request Authorization header

The `Authorization: Bearer` header **must** be set on each `HttpRequestMessage`, not on `HttpClient.DefaultRequestHeaders`. Setting it as a default header does not work — the API returns 429 immediately.

```csharp
// WRONG — causes instant 429
httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

// CORRECT — set per request
var request = new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages");
request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
```

### Required system prompt

The system prompt must include the Claude Code identity as a text block array (not a plain string):

```json
"system": [
  {"type": "text", "text": "You are Claude Code, Anthropic's official CLI for Claude."},
  {"type": "text", "text": "Your actual system prompt here"}
]
```

This is required — without it, requests may be rejected.

### Adaptive thinking for Claude 4.6 models

For `claude-sonnet-4-6` and `claude-opus-4-6`, include adaptive thinking:

```json
"thinking": {"type": "adaptive"}
```

When adaptive thinking is enabled, `temperature` cannot be set (it must be 1, which is the default). Setting any other temperature value returns a 400 error.

### Streaming

Streaming (`"stream": true`) is **not required** for OAuth tokens. Non-streaming requests work fine. OpenClaw always streams, but we confirmed non-streaming works during our testing.

### Complete request example

```json
POST https://api.anthropic.com/v1/messages

Headers:
  Authorization: Bearer sk-ant-oat01-...
  anthropic-version: 2023-06-01
  anthropic-beta: claude-code-20250219,oauth-2025-04-20,fine-grained-tool-streaming-2025-05-14
  x-app: cli
  user-agent: claude-cli/2.1.91
  anthropic-dangerous-direct-browser-access: true
  accept: application/json
  Content-Type: application/json

Body:
{
  "model": "claude-sonnet-4-6",
  "max_tokens": 16000,
  "system": [
    {"type": "text", "text": "You are Claude Code, Anthropic's official CLI for Claude."},
    {"type": "text", "text": "You summarize meeting transcripts."}
  ],
  "thinking": {"type": "adaptive"},
  "messages": [
    {"role": "user", "content": "Summarize this meeting..."}
  ]
}
```

### C# implementation

```csharp
// Configure HttpClient with default headers (everything except Authorization)
services.AddHttpClient("anthropic", client =>
{
    client.Timeout = TimeSpan.FromMinutes(5);
    client.DefaultRequestHeaders.Add("anthropic-version", "2023-06-01");
    client.DefaultRequestHeaders.Add("anthropic-beta",
        "claude-code-20250219,oauth-2025-04-20,fine-grained-tool-streaming-2025-05-14");
    client.DefaultRequestHeaders.Add("x-app", "cli");
    client.DefaultRequestHeaders.Add("anthropic-dangerous-direct-browser-access", "true");
    client.DefaultRequestHeaders.Accept.Add(
        new MediaTypeWithQualityHeaderValue("application/json"));
    client.DefaultRequestHeaders.UserAgent.ParseAdd("claude-cli/2.1.91");
});

// When sending a request — set Authorization per-request
var request = new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages");
request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", setupToken);
request.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");

var response = await httpClient.SendAsync(request);
```

### Building the request body

```csharp
var body = new Dictionary<string, object>
{
    ["model"] = "claude-sonnet-4-6",
    ["max_tokens"] = 16000,
    ["messages"] = new[] { new { role = "user", content = userMessage } },
    // System prompt MUST be a text block array for OAuth
    ["system"] = new[]
    {
        new { type = "text", text = "You are Claude Code, Anthropic's official CLI for Claude." },
        new { type = "text", text = yourSystemPrompt }
    },
    // Adaptive thinking for 4.6 models
    ["thinking"] = new { type = "adaptive" }
};
```

## Troubleshooting

**Instant 429 (rate_limit_error) with "Error" message**

Not an actual rate limit — the request is being rejected before reaching the model. Check:
1. `Authorization` header is set per-request, not on `DefaultRequestHeaders`
2. All three beta headers are present and in the correct order
3. `user-agent` contains a real Claude Code version number
4. System prompt uses text block array format

**401 Unauthorized**

Token expired or revoked. Generate a fresh one with `claude setup-token`.

**400 Bad Request: "temperature may only be set to 1 when thinking is enabled"**

Remove the `temperature` parameter or set it to 1. Adaptive thinking requires temperature = 1.

**400 Bad Request: "OAuth authentication is currently not supported"**

Missing the `anthropic-beta` header with the OAuth and Claude Code beta flags.

**Concurrent session conflicts**

If another Claude Code session (CLI, desktop, or web) is active on the same subscription, you may get 429 errors. Close other sessions or wait.

## Differences from API key requests

| Aspect | API key | Setup-token |
|--------|---------|-------------|
| Auth header | `x-api-key: sk-ant-api-...` | `Authorization: Bearer sk-ant-oat01-...` (per-request) |
| Beta headers | None required | `claude-code-20250219,oauth-2025-04-20,fine-grained-tool-streaming-2025-05-14` |
| Identity headers | None | `x-app: cli`, `user-agent: claude-cli/x.x.x`, `anthropic-dangerous-direct-browser-access: true` |
| System prompt | Plain string or text blocks | Must be text block array with Claude Code identity prefix |
| Billing | Per-token API billing | Your Claude subscription |
| Auto-refresh | N/A (keys don't expire unless rotated) | No — manual renewal |
