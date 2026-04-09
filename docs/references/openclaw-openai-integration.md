# OpenClaw OpenAI Integration

OpenClaw implements OpenAI through two distinct provider plugins registered in `/extensions/openai/index.ts`:

1. **Direct API** (`openai`) - API key authentication against `api.openai.com`
2. **Codex** (`openai-codex`) - OAuth authentication against ChatGPT backend (`chatgpt.com`)

Both share a common transport layer (`/src/agents/openai-transport-stream.ts`) and stream wrapper pipeline (`/extensions/openai/stream-hooks.ts`), but differ in authentication, endpoints, and feature support.

---

## Quick Comparison

| Aspect | Direct API | Codex |
|--------|-----------|-------|
| **Provider ID** | `openai` | `openai-codex` |
| **Auth** | API Key (`OPENAI_API_KEY`) | OAuth 2.0 (browser sign-in) |
| **Base URL** | `https://api.openai.com/v1` | `https://chatgpt.com/backend-api` |
| **Primary Endpoint** | `/responses` | `/response` |
| **Default Model** | `openai/gpt-5.4` | `openai-codex/gpt-5.4` |
| **Transport** | WebSocket + SSE + HTTP | WebSocket + SSE |
| **Audio Support** | Transcription + TTS | No |
| **Native Web Search** | No | Yes |
| **Service Tiers** | Flex / Default / Priority | Default only |
| **Cost Tracking** | Built-in per-model pricing | Usage API at `/wham/usage` |

---

## 1. Direct API Provider (`openai`)

### Key Files

- `/extensions/openai/openai-provider.ts` - Provider definition, auth config, model resolution, pricing
- `/extensions/openai/default-models.ts` - Default model catalog
- `/extensions/openai/shared.ts` - Shared utilities (URL validation, etc.)
- `/src/agents/openai-transport-stream.ts` - HTTP and WebSocket transport (~1262 lines)
- `/src/agents/openai-ws-connection.ts` - WebSocket connection manager with auto-reconnect
- `/src/agents/openai-ws-stream.ts` - WebSocket streaming wrapper
- `/src/agents/openai-ws-message-conversion.ts` - Message format conversion
- `/src/agents/openai-completions-compat.ts` - Legacy Chat Completions compatibility
- `/src/gateway/openai-http.ts` - HTTP compatibility layer (OpenAI-compatible server mode)

### Authentication

API key via environment variable or config option:

- **Env var:** `OPENAI_API_KEY`
- **Config key:** `openaiApiKey`
- Auth configuration at `openai-provider.ts:206-226`

### Endpoints Used

| Endpoint | Purpose |
|----------|---------|
| `POST /responses` | Primary - Responses API (streaming via WebSocket or SSE) |
| `POST /chat/completions` | Legacy fallback for simple requests |
| `POST /images/generations` | Image generation |
| `POST /audio/speech` | Text-to-speech |
| `POST /audio/transcriptions` | Audio transcription |
| `POST /embeddings` | Text embeddings |

### Transport Modes

Configured via `prepareExtraParams` (`openai-provider.ts:240-253`):

1. **WebSocket** (`transport: "websocket"`) - Default for Responses API. Persistent connection with auto-reconnect (exponential backoff: 1s/2s/4s/8s/16s, max 5 retries). Tracks `previous_response_id` for incremental tool results.
2. **SSE** (`transport: "sse"`) - HTTP streaming fallback.
3. **Auto** (`transport: "auto"`) - Tries WebSocket, falls back to HTTP on failure.

### Request Format (Responses API)

Uses OpenAI SDK types (`ResponseCreateParamsStreaming`). Key payload fields:

- `model` - Model identifier
- `messages` - `ResponseInput[]` via `convertResponsesMessages()`
- `tools` - `FunctionTool[]` with strict mode support
- `temperature`, `max_tokens` / `max_completion_tokens`
- `reasoning_effort` - `"minimal" | "low" | "medium" | "high" | "xhigh"`
- `service_tier` - `"flex" | "default" | "priority"`
- `store` - Prompt caching toggle
- `stream` - Streaming toggle

### Response Parsing (WebSocket Events)

From `openai-ws-connection.ts`:

- `response.created`, `response.in_progress`, `response.completed`, `response.failed`
- `response.output_item.added`, `response.output_item.done`
- `response.output_text.delta`, `response.output_text.done`
- `response.function_call_arguments.delta`, `response.function_call_arguments.done`
- `response.reasoning_summary_text.delta`
- `rate_limits.updated`
- `UsageInfo` with `input_tokens`, `output_tokens`, `total_tokens`

### Model Catalog

Dynamic resolution via `resolveDynamicModel()` (`openai-provider.ts:99-175`):

| Model | Context Window | Max Output |
|-------|---------------|------------|
| `gpt-5.4` | 272,000 | 128,000 |
| `gpt-5.4-pro` | 1,050,000 | - |
| `gpt-5.4-mini` | 400,000 | - |
| `gpt-5.4-nano` | 400,000 | - |
| Legacy: `gpt-5.2`, `gpt-5` | Varies | Varies |

### Pricing

From `openai-provider.ts:32-45`:

| Model | Input | Output | Cache Read |
|-------|-------|--------|------------|
| `gpt-5.4` | $2.50/1M | $15/1M | $0.25/1M |
| `gpt-5.4-pro` | $30/1M | $180/1M | - |

Service tier multipliers: flex (0.5x), default (1x), priority (2x).

### Error Handling

- WebSocket errors parsed from event with `code`/`message` fields (`openai-transport-stream.ts:511-527`)
- Auto mode: WebSocket failures gracefully degrade to HTTP (`openai-transport-stream.ts:621-638`)
- Abort signal support for request cancellation

---

## 2. Codex Provider (`openai-codex`)

### Key Files

- `/extensions/openai/openai-codex-provider.ts` - Provider definition (~327 lines)
- `/extensions/openai/openai-codex-catalog.ts` - Model catalog builder
- `/extensions/openai/openai-codex-auth-identity.ts` - JWT payload parsing
- `/extensions/openai/openai-codex-provider.runtime.ts` - Runtime OAuth helpers
- `/src/plugins/provider-openai-codex-oauth.ts` - OAuth flow implementation
- `/src/commands/openai-codex-oauth.ts` - CLI OAuth command
- `/src/infra/provider-usage.fetch.codex.ts` - Usage/rate-limit tracking
- `/src/agents/pi-embedded-runner/openai-stream-wrappers.ts` - Codex stream wrappers

### Authentication (OAuth 2.0)

Full browser-based OAuth flow (`provider-openai-codex-oauth.ts:1-81`):

1. Pre-flight TLS certificate check (`provider-openai-codex-oauth-tls.ts`)
2. User instructions displayed
3. Callback handlers created via `createVpsAwareOAuthHandlers()`
4. OAuth initiated via `loginOpenAICodex()` from `@mariozechner/pi-ai/oauth`
5. Optional manual code input for remote/VPS environments
6. Credentials returned: `access`, `refresh`, `expires` tokens
7. Identity resolved from JWT (`resolveCodexAuthIdentity()`)

**JWT Claims** (`openai-codex-auth-identity.ts:22-78`):

- `iss`, `sub`
- `https://api.openai.com/profile.email`
- `https://api.openai.com/auth.chatgpt_account_user_id` (primary)
- `https://api.openai.com/auth.chatgpt_user_id` (fallback)

**Callback:** Default `localhost:1455` for local browser; manual URL paste-back for VPS/remote.

### Endpoints Used

| Endpoint | Purpose |
|----------|---------|
| `POST /response` (on `chatgpt.com/backend-api`) | Primary - Responses API (WebSocket or SSE) |
| `GET /wham/usage` (on `chatgpt.com/backend-api`) | Rate limit & usage tracking |

### Model Catalog

Dynamic resolution via `openai-codex-provider.ts:92-146`:

| Model | Context Window |
|-------|---------------|
| `gpt-5.4` | 272,000 |
| `gpt-5.3-codex` | - |
| `gpt-5.3-codex-spark` | 128,000 |
| `gpt-5.2-codex` | - |
| Legacy: `gpt-5.1-codex` | - |

### Usage Tracking

`/src/infra/provider-usage.fetch.codex.ts` calls `https://chatgpt.com/backend-api/wham/usage`:

**Request headers:**
```
Authorization: Bearer {token}
User-Agent: CodexBar
Accept: application/json
ChatGPT-Account-Id: {accountId}  (optional)
```

**Response shape:**
```typescript
{
  rate_limit: {
    primary_window: {
      limit_window_seconds: number,
      used_percent: number,
      reset_at: number,
      reset_after_seconds: number
    },
    secondary_window: { /* same shape */ }
  },
  plan_type: string,
  credits: { balance: number | string }
}
```

### Codex-Exclusive Features

1. **Native Web Search** - ChatGPT's built-in search, activated via `resolveCodexNativeSearchActivation()` in `openai-stream-wrappers.ts:297-314`, injected via `patchCodexNativeWebSearchPayload()`.
2. **Text Verbosity Control** - Always overrides verbosity for Codex via `createOpenAITextVerbosityWrapper()` (`openai-stream-wrappers.ts:263-291`).

---

## 3. Shared Architecture

### Plugin Registration

`/extensions/openai/index.ts`:

```typescript
export default definePluginEntry({
  id: "openai",
  register(api) {
    api.registerProvider(buildOpenAIProvider());             // Direct API
    api.registerProvider(buildOpenAICodexProviderPlugin());   // Codex
    api.registerSpeechProvider(buildOpenAISpeechProvider());
    api.registerMediaUnderstandingProvider(openaiMediaUnderstandingProvider);
    api.registerMediaUnderstandingProvider(openaiCodexMediaUnderstandingProvider);
  }
});
```

### Stream Wrapper Pipeline

Applied to both providers via `/extensions/openai/stream-hooks.ts`:

1. `createOpenAIAttributionHeadersWrapper` - Attribution headers
2. `createOpenAIFastModeWrapper` - Fast mode (priority service tier)
3. `createOpenAIServiceTierWrapper` - Service tier config
4. `createOpenAITextVerbosityWrapper` - Text verbosity control
5. `createCodexNativeWebSearchWrapper` - Web search (Codex only)
6. `createOpenAIResponsesContextManagementWrapper` - Context/store management
7. `createOpenAIReasoningCompatibilityWrapper` - Reasoning payload fixes

### Payload Policy System

`/src/agents/openai-responses-payload-policy.ts` controls per-endpoint feature compatibility:

- `allowsServiceTier` - Service tier support
- `explicitStore` - Whether to include `store` field
- `shouldStripStore` - Remove store for incompatible endpoints
- `shouldStripPromptCache` - Remove prompt cache fields
- `shouldStripDisabledReasoningPayload` - Strip `reasoning: "none"` for GPT-5
- `useServerCompaction` - Server-side context compaction (OpenAI only)
- `compactThreshold` - Context size trigger (70% of window)

### Compatibility Layer

`/src/agents/openai-completions-compat.ts` provides a decision matrix per endpoint class:

- `supportsStore`, `supportsDeveloperRole`, `supportsReasoningEffort`
- `supportsUsageInStreaming`, `maxTokensField` (`max_tokens` vs `max_completion_tokens`)
- `thinkingFormat` (`openai` / `openrouter` / `zai`), `supportsStrictMode`

### SDK Dependencies

From `openai-transport-stream.ts:1-31`:

```typescript
import OpenAI, { AzureOpenAI } from "openai";
import type { ResponseCreateParamsStreaming, ... } from "openai/resources/responses/responses.js";
```

Also uses `@mariozechner/pi-ai/oauth` for Codex OAuth.

### Media Understanding

Both providers support image description via `describeImageWithModel()` / `describeImagesWithModel()` in `/extensions/openai/media-understanding-provider.ts`. Audio transcription is Direct API only.

### Speech / TTS (Direct API only)

`/extensions/openai/tts.ts`:

- Models: `gpt-4o-mini-tts`, `tts-1`, `tts-1-hd`
- 14 voices: alloy, ash, ballad, cedar, coral, echo, fable, juniper, marin, onyx, nova, sage, shimmer, verse

---

## 4. Environment Variables

| Variable | Provider | Purpose |
|----------|----------|---------|
| `OPENAI_API_KEY` | Direct API | API key authentication |
| `AZURE_OPENAI_API_VERSION` | Direct API (Azure) | Azure API version (default: `2024-12-01-preview`) |
| `OPENAI_TTS_BASE_URL` | Direct API | Custom TTS endpoint |

Codex uses OAuth tokens stored via the credential system rather than env vars.

---

## 5. Test Files

| File | Coverage |
|------|----------|
| `/extensions/openai/openai-provider.test.ts` | Provider logic, model resolution |
| `/extensions/openai/openai-codex-provider.test.ts` | Codex logic, OAuth, model resolution |
| `/extensions/openai/openai-provider.live.test.ts` | Live API integration |
| `/src/agents/openai-transport-stream.test.ts` | Transport layer, payload building |
| `/src/agents/openai-ws-stream.test.ts` | WebSocket streaming |

Key test scenarios: model resolution, wrapper composition, payload policy, WebSocket fallback, OAuth credential refresh, usage API parsing.
