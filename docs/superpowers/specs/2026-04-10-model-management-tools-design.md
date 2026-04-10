# LLM Model Management Tools

GitHub Issue: [#3 — LLM model management tools](https://github.com/mbundgaard/OpenAgent/issues/3)

## Summary

Three tools for runtime per-conversation model selection (text LLM only). The agent can list available models, check which model the current conversation uses, and switch to a different one mid-conversation.

## Project

New project: `OpenAgent.Tools.ModelManagement`, following the same pattern as `Tools.FileSystem`, `Tools.Shell`, etc. References `OpenAgent.Contracts` and `OpenAgent.Models`.

## Tools

### `get_available_models`

Lists all models from all configured text LLM providers.

- **Parameters**: none
- **Returns**: JSON array grouped by provider, only including providers with at least one model configured

```json
[
  {
    "provider": "anthropic-subscription",
    "models": ["claude-opus-4-5", "claude-opus-4-6", "claude-sonnet-4-5", "claude-sonnet-4-6", "claude-haiku-4-5"]
  },
  {
    "provider": "azure-openai-text",
    "models": ["gpt-4o"]
  }
]
```

### `get_current_model`

Returns the active provider and model for the current conversation.

- **Parameters**: none (uses `conversationId` from `ExecuteAsync`)
- **Returns**: provider and model for the conversation

```json
{ "provider": "anthropic-subscription", "model": "claude-sonnet-4-6" }
```

- **Error**: conversation not found — returns error message

### `set_model`

Changes the model for the current conversation. Takes effect on the next LLM call (the current response completes with the old model).

- **Parameters**: `provider` (string, required), `model` (string, required)
- **Returns**: confirmation message with old and new provider/model
- **Validation**:
  - Unknown provider — error listing valid providers
  - Unknown model — error listing valid models for that provider
- **Persistence**: updates `conversation.Provider` and `conversation.Model` via `IConversationStore.Update()`

## Architecture

### `ModelToolHandler`

Implements `IToolHandler`. Constructor receives:
- `IConversationStore` — to read/update conversation provider and model
- `IEnumerable<ILlmTextProvider>` — cast to `IConfigurable` to access `Key` and `Models`

Creates the three tool instances, passing dependencies through.

### Why per-conversation works

Conversations already store `Provider` and `Model` fields (set from `AgentConfig` defaults at creation time). `ChatEndpoints` and `WebSocketTextEndpoints` resolve the provider from `conversation.Provider` at call time — so updating these fields on the conversation is sufficient for the change to take effect.

### Identifier format

Provider and model are kept as separate fields throughout — in tool parameters, return values, and conversation storage. No composite `provider/model` strings.

## DI Registration

Register `ModelToolHandler` as `IToolHandler` in `Program.cs`, same pattern as other tool handlers. The handler receives `IConversationStore` and all `ILlmTextProvider` instances via constructor injection.

## Testing

Integration test in `OpenAgent.Tests` verifying:
- `get_available_models` returns configured providers and their models
- `get_current_model` returns the conversation's current provider/model
- `set_model` updates the conversation and the change persists
- `set_model` with invalid provider/model returns helpful error messages
