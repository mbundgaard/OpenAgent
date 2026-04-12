# Model Management Tools Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Three agent tools (`get_available_models`, `get_current_model`, `set_model`) for per-conversation model selection at runtime.

**Architecture:** New `OpenAgent.Tools.ModelManagement` project with a `ModelToolHandler` implementing `IToolHandler`. The handler receives all text providers (as `IConfigurable` with `Key` and `Models`) and `IConversationStore`. Tools use `conversationId` (passed to `ExecuteAsync`) to read/write the conversation's `Provider` and `Model` fields. DI registration forwards both keyed text providers as non-keyed so `IEnumerable<ILlmTextProvider>` resolves all of them.

**Tech Stack:** .NET 10, xUnit, System.Text.Json

**Spec:** `docs/superpowers/specs/2026-04-10-model-management-tools-design.md`

---

### Task 1: Create project and wire into solution

**Files:**
- Create: `src/agent/OpenAgent.Tools.ModelManagement/OpenAgent.Tools.ModelManagement.csproj`
- Modify: `src/agent/OpenAgent.sln`
- Modify: `src/agent/OpenAgent/OpenAgent.csproj` (add project reference)
- Modify: `src/agent/OpenAgent.Tests/OpenAgent.Tests.csproj` (add project reference)

- [ ] **Step 1: Create the .csproj**

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <ItemGroup>
    <ProjectReference Include="..\OpenAgent.Contracts\OpenAgent.Contracts.csproj" />
  </ItemGroup>

  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>

</Project>
```

- [ ] **Step 2: Add project to solution and references**

```bash
cd src/agent
dotnet sln add OpenAgent.Tools.ModelManagement/OpenAgent.Tools.ModelManagement.csproj
dotnet add OpenAgent/OpenAgent.csproj reference OpenAgent.Tools.ModelManagement/OpenAgent.Tools.ModelManagement.csproj
dotnet add OpenAgent.Tests/OpenAgent.Tests.csproj reference OpenAgent.Tools.ModelManagement/OpenAgent.Tools.ModelManagement.csproj
```

- [ ] **Step 3: Verify it builds**

```bash
cd src/agent && dotnet build
```

Expected: Build succeeded.

- [ ] **Step 4: Commit**

```bash
git add src/agent/OpenAgent.Tools.ModelManagement/ src/agent/OpenAgent.sln src/agent/OpenAgent/OpenAgent.csproj src/agent/OpenAgent.Tests/OpenAgent.Tests.csproj
git commit -m "feat: add OpenAgent.Tools.ModelManagement project skeleton"
```

---

### Task 2: Implement `GetAvailableModelsTool`

**Files:**
- Create: `src/agent/OpenAgent.Tools.ModelManagement/GetAvailableModelsTool.cs`
- Test: `src/agent/OpenAgent.Tests/ModelManagement/GetAvailableModelsToolTests.cs`

- [ ] **Step 1: Write the failing test**

Create `src/agent/OpenAgent.Tests/ModelManagement/GetAvailableModelsToolTests.cs`:

```csharp
using System.Text.Json;
using OpenAgent.Contracts;
using OpenAgent.Models.Providers;
using OpenAgent.Tools.ModelManagement;

namespace OpenAgent.Tests.ModelManagement;

public class GetAvailableModelsToolTests
{
    [Fact]
    public async Task ReturnsModelsGroupedByProvider()
    {
        var providers = new ILlmTextProvider[]
        {
            new FakeModelProvider("provider-a", ["model-1", "model-2"]),
            new FakeModelProvider("provider-b", ["model-3"])
        };
        var tool = new GetAvailableModelsTool(providers);

        var result = await tool.ExecuteAsync("{}", "conv-1");
        var doc = JsonDocument.Parse(result);
        var root = doc.RootElement;

        Assert.Equal(2, root.GetArrayLength());

        var first = root[0];
        Assert.Equal("provider-a", first.GetProperty("provider").GetString());
        var models = first.GetProperty("models");
        Assert.Equal(2, models.GetArrayLength());
        Assert.Equal("model-1", models[0].GetString());
        Assert.Equal("model-2", models[1].GetString());

        var second = root[1];
        Assert.Equal("provider-b", second.GetProperty("provider").GetString());
        Assert.Equal(1, second.GetProperty("models").GetArrayLength());
    }

    [Fact]
    public async Task ExcludesProvidersWithNoModels()
    {
        var providers = new ILlmTextProvider[]
        {
            new FakeModelProvider("configured", ["model-1"]),
            new FakeModelProvider("unconfigured", [])
        };
        var tool = new GetAvailableModelsTool(providers);

        var result = await tool.ExecuteAsync("{}", "conv-1");
        var doc = JsonDocument.Parse(result);

        Assert.Equal(1, doc.RootElement.GetArrayLength());
        Assert.Equal("configured", doc.RootElement[0].GetProperty("provider").GetString());
    }
}
```

Also create the `FakeModelProvider` helper used by all tests in this folder. Create `src/agent/OpenAgent.Tests/ModelManagement/FakeModelProvider.cs`:

```csharp
using System.Text.Json;
using OpenAgent.Contracts;
using OpenAgent.Models.Common;
using OpenAgent.Models.Conversations;
using OpenAgent.Models.Providers;

namespace OpenAgent.Tests.ModelManagement;

/// <summary>
/// Minimal ILlmTextProvider fake that exposes a key and model list for model management tool tests.
/// </summary>
internal sealed class FakeModelProvider(string key, string[] models) : ILlmTextProvider
{
    public string Key => key;
    public IReadOnlyList<ProviderConfigField> ConfigFields => [];
    public IReadOnlyList<string> Models => models;
    public void Configure(JsonElement configuration) { }

    public IAsyncEnumerable<CompletionEvent> CompleteAsync(
        Conversation conversation, Message userMessage, CancellationToken ct = default)
        => throw new NotImplementedException();

    public IAsyncEnumerable<CompletionEvent> CompleteAsync(
        IReadOnlyList<Message> messages, string model,
        CompletionOptions? options = null, CancellationToken ct = default)
        => throw new NotImplementedException();
}
```

- [ ] **Step 2: Run test to verify it fails**

```bash
cd src/agent && dotnet test --filter "GetAvailableModelsToolTests" --no-restore
```

Expected: FAIL — `GetAvailableModelsTool` does not exist.

- [ ] **Step 3: Write the implementation**

Create `src/agent/OpenAgent.Tools.ModelManagement/GetAvailableModelsTool.cs`:

```csharp
using System.Text.Json;
using OpenAgent.Contracts;

namespace OpenAgent.Tools.ModelManagement;

/// <summary>
/// Returns all available models from all configured text LLM providers.
/// </summary>
public sealed class GetAvailableModelsTool(IEnumerable<ILlmTextProvider> providers) : ITool
{
    public AgentToolDefinition Definition { get; } = new()
    {
        Name = "get_available_models",
        Description = "List all available text LLM models grouped by provider.",
        Parameters = new
        {
            type = "object",
            properties = new { },
            required = Array.Empty<string>()
        }
    };

    public Task<string> ExecuteAsync(string arguments, string conversationId, CancellationToken ct = default)
    {
        var result = providers
            .Where(p => p.Models.Count > 0)
            .Select(p => new { provider = p.Key, models = p.Models })
            .ToList();

        return Task.FromResult(JsonSerializer.Serialize(result));
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

```bash
cd src/agent && dotnet test --filter "GetAvailableModelsToolTests" --no-restore
```

Expected: 2 tests PASS.

- [ ] **Step 5: Commit**

```bash
git add src/agent/OpenAgent.Tools.ModelManagement/GetAvailableModelsTool.cs src/agent/OpenAgent.Tests/ModelManagement/
git commit -m "feat: add get_available_models tool"
```

---

### Task 3: Implement `GetCurrentModelTool`

**Files:**
- Create: `src/agent/OpenAgent.Tools.ModelManagement/GetCurrentModelTool.cs`
- Test: `src/agent/OpenAgent.Tests/ModelManagement/GetCurrentModelToolTests.cs`

- [ ] **Step 1: Write the failing test**

Create `src/agent/OpenAgent.Tests/ModelManagement/GetCurrentModelToolTests.cs`:

```csharp
using System.Text.Json;
using OpenAgent.Models.Conversations;
using OpenAgent.Tests.Fakes;
using OpenAgent.Tools.ModelManagement;

namespace OpenAgent.Tests.ModelManagement;

public class GetCurrentModelToolTests
{
    [Fact]
    public async Task ReturnsConversationProviderAndModel()
    {
        var store = new InMemoryConversationStore();
        store.GetOrCreate("conv-1", "app", ConversationType.Text, "anthropic-subscription", "claude-sonnet-4-6");
        var tool = new GetCurrentModelTool(store);

        var result = await tool.ExecuteAsync("{}", "conv-1");
        var doc = JsonDocument.Parse(result);

        Assert.Equal("anthropic-subscription", doc.RootElement.GetProperty("provider").GetString());
        Assert.Equal("claude-sonnet-4-6", doc.RootElement.GetProperty("model").GetString());
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

```bash
cd src/agent && dotnet test --filter "GetCurrentModelToolTests" --no-restore
```

Expected: FAIL — `GetCurrentModelTool` does not exist.

- [ ] **Step 3: Write the implementation**

Create `src/agent/OpenAgent.Tools.ModelManagement/GetCurrentModelTool.cs`:

```csharp
using System.Text.Json;
using OpenAgent.Contracts;

namespace OpenAgent.Tools.ModelManagement;

/// <summary>
/// Returns the active provider and model for the current conversation.
/// </summary>
public sealed class GetCurrentModelTool(IConversationStore store) : ITool
{
    public AgentToolDefinition Definition { get; } = new()
    {
        Name = "get_current_model",
        Description = "Get the active text LLM provider and model for the current conversation.",
        Parameters = new
        {
            type = "object",
            properties = new { },
            required = Array.Empty<string>()
        }
    };

    public Task<string> ExecuteAsync(string arguments, string conversationId, CancellationToken ct = default)
    {
        var conversation = store.Get(conversationId);
        if (conversation is null)
            return Task.FromResult(JsonSerializer.Serialize(new { error = $"Conversation '{conversationId}' not found." }));

        var result = new { provider = conversation.Provider, model = conversation.Model };
        return Task.FromResult(JsonSerializer.Serialize(result));
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

```bash
cd src/agent && dotnet test --filter "GetCurrentModelToolTests" --no-restore
```

Expected: 1 test PASS.

- [ ] **Step 5: Commit**

```bash
git add src/agent/OpenAgent.Tools.ModelManagement/GetCurrentModelTool.cs src/agent/OpenAgent.Tests/ModelManagement/GetCurrentModelToolTests.cs
git commit -m "feat: add get_current_model tool"
```

---

### Task 4: Implement `SetModelTool`

**Files:**
- Create: `src/agent/OpenAgent.Tools.ModelManagement/SetModelTool.cs`
- Test: `src/agent/OpenAgent.Tests/ModelManagement/SetModelToolTests.cs`

- [ ] **Step 1: Write the failing tests**

Create `src/agent/OpenAgent.Tests/ModelManagement/SetModelToolTests.cs`:

```csharp
using System.Text.Json;
using OpenAgent.Models.Conversations;
using OpenAgent.Tests.Fakes;
using OpenAgent.Tools.ModelManagement;
using OpenAgent.Contracts;

namespace OpenAgent.Tests.ModelManagement;

public class SetModelToolTests
{
    private readonly InMemoryConversationStore _store = new();
    private readonly ILlmTextProvider[] _providers =
    [
        new FakeModelProvider("provider-a", ["model-1", "model-2"]),
        new FakeModelProvider("provider-b", ["model-3"])
    ];

    [Fact]
    public async Task UpdatesConversationProviderAndModel()
    {
        _store.GetOrCreate("conv-1", "app", ConversationType.Text, "provider-a", "model-1");
        var tool = new SetModelTool(_store, _providers);

        var result = await tool.ExecuteAsync(
            JsonSerializer.Serialize(new { provider = "provider-b", model = "model-3" }), "conv-1");
        var doc = JsonDocument.Parse(result);

        // Tool returns confirmation with old and new values
        Assert.Equal("provider-a", doc.RootElement.GetProperty("previous_provider").GetString());
        Assert.Equal("model-1", doc.RootElement.GetProperty("previous_model").GetString());
        Assert.Equal("provider-b", doc.RootElement.GetProperty("provider").GetString());
        Assert.Equal("model-3", doc.RootElement.GetProperty("model").GetString());

        // Verify the conversation was actually updated in the store
        var conversation = _store.Get("conv-1")!;
        Assert.Equal("provider-b", conversation.Provider);
        Assert.Equal("model-3", conversation.Model);
    }

    [Fact]
    public async Task RejectsUnknownProvider()
    {
        _store.GetOrCreate("conv-1", "app", ConversationType.Text, "provider-a", "model-1");
        var tool = new SetModelTool(_store, _providers);

        var result = await tool.ExecuteAsync(
            JsonSerializer.Serialize(new { provider = "unknown", model = "model-1" }), "conv-1");
        var doc = JsonDocument.Parse(result);

        Assert.Contains("unknown", doc.RootElement.GetProperty("error").GetString());
        Assert.True(doc.RootElement.TryGetProperty("available_providers", out _));

        // Verify the conversation was NOT changed
        var conversation = _store.Get("conv-1")!;
        Assert.Equal("provider-a", conversation.Provider);
        Assert.Equal("model-1", conversation.Model);
    }

    [Fact]
    public async Task RejectsUnknownModel()
    {
        _store.GetOrCreate("conv-1", "app", ConversationType.Text, "provider-a", "model-1");
        var tool = new SetModelTool(_store, _providers);

        var result = await tool.ExecuteAsync(
            JsonSerializer.Serialize(new { provider = "provider-a", model = "nonexistent" }), "conv-1");
        var doc = JsonDocument.Parse(result);

        Assert.Contains("nonexistent", doc.RootElement.GetProperty("error").GetString());
        Assert.True(doc.RootElement.TryGetProperty("available_models", out _));

        // Verify the conversation was NOT changed
        var conversation = _store.Get("conv-1")!;
        Assert.Equal("provider-a", conversation.Provider);
        Assert.Equal("model-1", conversation.Model);
    }

    [Fact]
    public async Task DoesNotAffectOtherConversations()
    {
        _store.GetOrCreate("conv-1", "app", ConversationType.Text, "provider-a", "model-1");
        _store.GetOrCreate("conv-2", "app", ConversationType.Text, "provider-a", "model-1");
        var tool = new SetModelTool(_store, _providers);

        await tool.ExecuteAsync(
            JsonSerializer.Serialize(new { provider = "provider-b", model = "model-3" }), "conv-1");

        // conv-2 should be unchanged
        var conv2 = _store.Get("conv-2")!;
        Assert.Equal("provider-a", conv2.Provider);
        Assert.Equal("model-1", conv2.Model);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

```bash
cd src/agent && dotnet test --filter "SetModelToolTests" --no-restore
```

Expected: FAIL — `SetModelTool` does not exist.

- [ ] **Step 3: Write the implementation**

Create `src/agent/OpenAgent.Tools.ModelManagement/SetModelTool.cs`:

```csharp
using System.Text.Json;
using OpenAgent.Contracts;

namespace OpenAgent.Tools.ModelManagement;

/// <summary>
/// Changes the text LLM provider and model for the current conversation.
/// Takes effect on the next LLM call.
/// </summary>
public sealed class SetModelTool(IConversationStore store, IEnumerable<ILlmTextProvider> providers) : ITool
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public AgentToolDefinition Definition { get; } = new()
    {
        Name = "set_model",
        Description = "Change the text LLM provider and model for the current conversation. Takes effect on the next message.",
        Parameters = new
        {
            type = "object",
            properties = new
            {
                provider = new { type = "string", description = "Provider key (e.g. 'anthropic-subscription')" },
                model = new { type = "string", description = "Model name (e.g. 'claude-sonnet-4-6')" }
            },
            required = new[] { "provider", "model" }
        }
    };

    public Task<string> ExecuteAsync(string arguments, string conversationId, CancellationToken ct = default)
    {
        var args = JsonDocument.Parse(arguments).RootElement;
        var providerKey = args.GetProperty("provider").GetString()!;
        var modelName = args.GetProperty("model").GetString()!;

        // Validate provider exists
        var providerList = providers.ToList();
        var targetProvider = providerList.FirstOrDefault(p => p.Key == providerKey);
        if (targetProvider is null)
        {
            var available = providerList.Select(p => p.Key).ToList();
            return Task.FromResult(JsonSerializer.Serialize(new
            {
                error = $"Unknown provider '{providerKey}'.",
                available_providers = available
            }));
        }

        // Validate model exists for that provider
        if (!targetProvider.Models.Contains(modelName))
        {
            return Task.FromResult(JsonSerializer.Serialize(new
            {
                error = $"Unknown model '{modelName}' for provider '{providerKey}'.",
                available_models = targetProvider.Models
            }));
        }

        // Load conversation and update
        var conversation = store.Get(conversationId);
        if (conversation is null)
            return Task.FromResult(JsonSerializer.Serialize(new { error = $"Conversation '{conversationId}' not found." }));

        var previousProvider = conversation.Provider;
        var previousModel = conversation.Model;

        conversation.Provider = providerKey;
        conversation.Model = modelName;
        store.Update(conversation);

        return Task.FromResult(JsonSerializer.Serialize(new
        {
            previous_provider = previousProvider,
            previous_model = previousModel,
            provider = providerKey,
            model = modelName
        }));
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

```bash
cd src/agent && dotnet test --filter "SetModelToolTests" --no-restore
```

Expected: 4 tests PASS.

- [ ] **Step 5: Commit**

```bash
git add src/agent/OpenAgent.Tools.ModelManagement/SetModelTool.cs src/agent/OpenAgent.Tests/ModelManagement/SetModelToolTests.cs
git commit -m "feat: add set_model tool with validation"
```

---

### Task 5: Create `ModelToolHandler` and register in DI

**Files:**
- Create: `src/agent/OpenAgent.Tools.ModelManagement/ModelToolHandler.cs`
- Modify: `src/agent/OpenAgent/Program.cs`

- [ ] **Step 1: Create the handler**

Create `src/agent/OpenAgent.Tools.ModelManagement/ModelToolHandler.cs`:

```csharp
using OpenAgent.Contracts;

namespace OpenAgent.Tools.ModelManagement;

/// <summary>
/// Groups model management tools (get_available_models, get_current_model, set_model)
/// under a single handler for DI registration.
/// </summary>
public sealed class ModelToolHandler : IToolHandler
{
    public IReadOnlyList<ITool> Tools { get; }

    public ModelToolHandler(IConversationStore store, IEnumerable<ILlmTextProvider> providers)
    {
        var providerList = providers.ToList();
        Tools =
        [
            new GetAvailableModelsTool(providerList),
            new GetCurrentModelTool(store),
            new SetModelTool(store, providerList)
        ];
    }
}
```

- [ ] **Step 2: Register in Program.cs**

In `src/agent/OpenAgent/Program.cs`, add the using at the top with the other tool imports:

```csharp
using OpenAgent.Tools.ModelManagement;
```

Then add the registration after the other `IToolHandler` registrations (after line 71 `AddSingleton<IToolHandler, ExpandToolHandler>()`):

```csharp
builder.Services.AddSingleton<IToolHandler, ModelToolHandler>();
```

- [ ] **Step 3: Fix DI — register both text providers as non-keyed**

Currently only one text provider is registered non-keyed (Program.cs line 90-91), so `IEnumerable<ILlmTextProvider>` only resolves one. Replace the single non-keyed forwarding with both providers.

In `src/agent/OpenAgent/Program.cs`, replace lines 89-91:

```csharp
// Non-keyed forwarding — endpoints and VoiceSessionManager resolve the default provider
builder.Services.AddSingleton<ILlmTextProvider>(sp =>
    sp.GetRequiredKeyedService<ILlmTextProvider>(AzureOpenAiTextProvider.ProviderKey));
```

With:

```csharp
// Non-keyed forwarding — IEnumerable<ILlmTextProvider> resolves all text providers
builder.Services.AddSingleton<ILlmTextProvider>(sp =>
    sp.GetRequiredKeyedService<ILlmTextProvider>(AzureOpenAiTextProvider.ProviderKey));
builder.Services.AddSingleton<ILlmTextProvider>(sp =>
    sp.GetRequiredKeyedService<ILlmTextProvider>(AnthropicSubscriptionTextProvider.ProviderKey));
```

- [ ] **Step 4: Build and run tests**

```bash
cd src/agent && dotnet build && dotnet test
```

Expected: All tests pass, including the new model management tests.

- [ ] **Step 5: Commit**

```bash
git add src/agent/OpenAgent.Tools.ModelManagement/ModelToolHandler.cs src/agent/OpenAgent/Program.cs
git commit -m "feat: register ModelToolHandler and wire DI for all text providers"
```

---

### Task 6: Verify end-to-end against running instance

This is a manual verification step — no new code.

- [ ] **Step 1: Restart the local dev server**

The server needs to pick up the new tool registrations.

- [ ] **Step 2: Verify tools are available**

Send a chat message asking the agent to list available models. The agent should use the `get_available_models` tool and return the configured providers and models.

- [ ] **Step 3: Verify model switching**

Ask the agent to switch to a different model (e.g. "switch to claude-opus-4-6"). The agent should use `set_model` and confirm the change.

- [ ] **Step 4: Verify the change persists**

Send another message and verify `get_current_model` shows the updated model. Check in the conversation response that the new model is being used.
