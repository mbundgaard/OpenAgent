# Multi-Connection Management — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Support multiple named server connections in the iOS app with a top-bar switcher, add/delete/rename in settings.

**Architecture:** Replace `QrPayload` with `ServerConnection(Id, Name, BaseUrl, Token)`. Store all connections as a JSON array in a single Keychain entry. Track active connection ID in `Preferences`. ConversationCache becomes per-connection. Settings page gets a connections management section. Top bar shows active connection name with a picker to switch.

**Tech Stack:** .NET MAUI, CommunityToolkit.Mvvm, iOS Keychain (Security framework), xUnit

---

### Task 1: ServerConnection model + IConnectionStore interface

**Files:**
- Create: `src/app/OpenAgent.App.Core/Services/ServerConnection.cs`
- Create: `src/app/OpenAgent.App.Core/Services/IConnectionStore.cs`
- Test: `src/app/OpenAgent.App.Tests/Services/InMemoryConnectionStoreTests.cs`

- [ ] **Step 1: Create the ServerConnection record**

```csharp
// src/app/OpenAgent.App.Core/Services/ServerConnection.cs
using System.Text.Json.Serialization;

namespace OpenAgent.App.Core.Services;

/// <summary>A named server connection with credentials.</summary>
public sealed record ServerConnection(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("base_url")] string BaseUrl,
    [property: JsonPropertyName("token")] string Token);
```

- [ ] **Step 2: Create the IConnectionStore interface**

```csharp
// src/app/OpenAgent.App.Core/Services/IConnectionStore.cs
namespace OpenAgent.App.Core.Services;

/// <summary>Manages multiple named server connections. iOS uses Keychain; tests use in-memory.</summary>
public interface IConnectionStore
{
    /// <summary>Returns all stored connections.</summary>
    Task<List<ServerConnection>> LoadAllAsync(CancellationToken ct = default);

    /// <summary>Returns the currently active connection, or null if none exist.</summary>
    Task<ServerConnection?> LoadActiveAsync(CancellationToken ct = default);

    /// <summary>Adds or updates a connection by its Id.</summary>
    Task SaveAsync(ServerConnection connection, CancellationToken ct = default);

    /// <summary>Removes a connection. Returns the number of connections remaining.</summary>
    Task<int> DeleteAsync(string connectionId, CancellationToken ct = default);

    /// <summary>Sets the active connection by Id.</summary>
    Task SetActiveAsync(string connectionId, CancellationToken ct = default);

    /// <summary>Returns the active connection Id, or null if none set.</summary>
    Task<string?> GetActiveIdAsync(CancellationToken ct = default);
}
```

- [ ] **Step 3: Commit**

```bash
git add src/app/OpenAgent.App.Core/Services/ServerConnection.cs src/app/OpenAgent.App.Core/Services/IConnectionStore.cs
git commit -m "feat(app): add ServerConnection model and IConnectionStore interface"
```

---

### Task 2: InMemoryConnectionStore + tests

**Files:**
- Create: `src/app/OpenAgent.App.Core/Services/InMemoryConnectionStore.cs`
- Create: `src/app/OpenAgent.App.Tests/Services/InMemoryConnectionStoreTests.cs`

- [ ] **Step 1: Write the failing tests**

```csharp
// src/app/OpenAgent.App.Tests/Services/InMemoryConnectionStoreTests.cs
using OpenAgent.App.Core.Services;

namespace OpenAgent.App.Tests.Services;

public class InMemoryConnectionStoreTests
{
    private static ServerConnection Conn(string id = "c1", string name = "Test", string url = "https://h/", string token = "tok")
        => new(id, name, url, token);

    [Fact]
    public async Task LoadAll_empty_returns_empty_list()
    {
        var store = new InMemoryConnectionStore();
        var all = await store.LoadAllAsync();
        Assert.Empty(all);
    }

    [Fact]
    public async Task Save_and_LoadAll_round_trips()
    {
        var store = new InMemoryConnectionStore();
        await store.SaveAsync(Conn());
        var all = await store.LoadAllAsync();
        Assert.Single(all);
        Assert.Equal("c1", all[0].Id);
        Assert.Equal("Test", all[0].Name);
    }

    [Fact]
    public async Task Save_updates_existing_by_id()
    {
        var store = new InMemoryConnectionStore();
        await store.SaveAsync(Conn());
        await store.SaveAsync(Conn(name: "Renamed"));
        var all = await store.LoadAllAsync();
        Assert.Single(all);
        Assert.Equal("Renamed", all[0].Name);
    }

    [Fact]
    public async Task Delete_removes_and_returns_remaining_count()
    {
        var store = new InMemoryConnectionStore();
        await store.SaveAsync(Conn("a"));
        await store.SaveAsync(Conn("b"));
        var remaining = await store.DeleteAsync("a");
        Assert.Equal(1, remaining);
        var all = await store.LoadAllAsync();
        Assert.Single(all);
        Assert.Equal("b", all[0].Id);
    }

    [Fact]
    public async Task Delete_nonexistent_returns_current_count()
    {
        var store = new InMemoryConnectionStore();
        await store.SaveAsync(Conn());
        var remaining = await store.DeleteAsync("nonexistent");
        Assert.Equal(1, remaining);
    }

    [Fact]
    public async Task SetActive_and_GetActiveId_round_trips()
    {
        var store = new InMemoryConnectionStore();
        await store.SaveAsync(Conn("a"));
        await store.SaveAsync(Conn("b"));
        await store.SetActiveAsync("b");
        Assert.Equal("b", await store.GetActiveIdAsync());
    }

    [Fact]
    public async Task LoadActive_returns_active_connection()
    {
        var store = new InMemoryConnectionStore();
        await store.SaveAsync(Conn("a", url: "https://a/"));
        await store.SaveAsync(Conn("b", url: "https://b/"));
        await store.SetActiveAsync("b");
        var active = await store.LoadActiveAsync();
        Assert.Equal("https://b/", active!.BaseUrl);
    }

    [Fact]
    public async Task LoadActive_returns_null_when_empty()
    {
        var store = new InMemoryConnectionStore();
        Assert.Null(await store.LoadActiveAsync());
    }

    [Fact]
    public async Task LoadActive_falls_back_to_first_when_active_id_missing()
    {
        var store = new InMemoryConnectionStore();
        await store.SaveAsync(Conn("a"));
        await store.SetActiveAsync("deleted");
        var active = await store.LoadActiveAsync();
        Assert.Equal("a", active!.Id);
    }

    [Fact]
    public async Task First_save_auto_sets_active()
    {
        var store = new InMemoryConnectionStore();
        await store.SaveAsync(Conn("first"));
        Assert.Equal("first", await store.GetActiveIdAsync());
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `cd src/app && dotnet test --filter InMemoryConnectionStoreTests`
Expected: build failure — `InMemoryConnectionStore` does not exist.

- [ ] **Step 3: Implement InMemoryConnectionStore**

```csharp
// src/app/OpenAgent.App.Core/Services/InMemoryConnectionStore.cs
namespace OpenAgent.App.Core.Services;

/// <summary>In-memory connection store for tests. Production iOS uses Keychain.</summary>
public sealed class InMemoryConnectionStore : IConnectionStore
{
    private readonly List<ServerConnection> _connections = new();
    private string? _activeId;

    public Task<List<ServerConnection>> LoadAllAsync(CancellationToken ct = default)
        => Task.FromResult(new List<ServerConnection>(_connections));

    public Task<ServerConnection?> LoadActiveAsync(CancellationToken ct = default)
    {
        if (_connections.Count == 0) return Task.FromResult<ServerConnection?>(null);
        var match = _activeId is not null ? _connections.Find(c => c.Id == _activeId) : null;
        return Task.FromResult<ServerConnection?>(match ?? _connections[0]);
    }

    public Task SaveAsync(ServerConnection connection, CancellationToken ct = default)
    {
        var idx = _connections.FindIndex(c => c.Id == connection.Id);
        if (idx >= 0) _connections[idx] = connection;
        else _connections.Add(connection);
        if (_activeId is null) _activeId = connection.Id;
        return Task.CompletedTask;
    }

    public Task<int> DeleteAsync(string connectionId, CancellationToken ct = default)
    {
        _connections.RemoveAll(c => c.Id == connectionId);
        if (_activeId == connectionId) _activeId = _connections.FirstOrDefault()?.Id;
        return Task.FromResult(_connections.Count);
    }

    public Task SetActiveAsync(string connectionId, CancellationToken ct = default)
    {
        _activeId = connectionId;
        return Task.CompletedTask;
    }

    public Task<string?> GetActiveIdAsync(CancellationToken ct = default)
        => Task.FromResult(_activeId);
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `cd src/app && dotnet test --filter InMemoryConnectionStoreTests`
Expected: all 10 tests PASS.

- [ ] **Step 5: Commit**

```bash
git add src/app/OpenAgent.App.Core/Services/InMemoryConnectionStore.cs src/app/OpenAgent.App.Tests/Services/InMemoryConnectionStoreTests.cs
git commit -m "feat(app): add InMemoryConnectionStore with tests"
```

---

### Task 3: IosKeychainConnectionStore

**Files:**
- Create: `src/app/OpenAgent.App/Platforms/iOS/IosKeychainConnectionStore.cs`
- Modify: `src/app/OpenAgent.App/Platforms/iOS/IosKeychainCredentialStore.cs` (delete later in Task 8)

- [ ] **Step 1: Create IosKeychainConnectionStore**

```csharp
// src/app/OpenAgent.App/Platforms/iOS/IosKeychainConnectionStore.cs
using System.Text.Json;
using Foundation;
using OpenAgent.App.Core.Services;
using Security;

namespace OpenAgent.App;

/// <summary>Stores connections as a JSON array in the iOS Keychain. Active ID in Preferences.</summary>
public sealed class IosKeychainConnectionStore : IConnectionStore
{
    private const string Service = "OpenAgent";
    private const string Account = "connections";
    private const string ActiveIdKey = "active_connection_id";

    public Task<List<ServerConnection>> LoadAllAsync(CancellationToken ct = default)
    {
        var list = ReadList();
        return Task.FromResult(list);
    }

    public Task<ServerConnection?> LoadActiveAsync(CancellationToken ct = default)
    {
        var list = ReadList();
        if (list.Count == 0) return Task.FromResult<ServerConnection?>(null);
        var activeId = Preferences.Default.Get<string?>(ActiveIdKey, null);
        var match = activeId is not null ? list.Find(c => c.Id == activeId) : null;
        return Task.FromResult<ServerConnection?>(match ?? list[0]);
    }

    public Task SaveAsync(ServerConnection connection, CancellationToken ct = default)
    {
        var list = ReadList();
        var idx = list.FindIndex(c => c.Id == connection.Id);
        if (idx >= 0) list[idx] = connection;
        else list.Add(connection);
        WriteList(list);
        if (!Preferences.Default.ContainsKey(ActiveIdKey))
            Preferences.Default.Set(ActiveIdKey, connection.Id);
        return Task.CompletedTask;
    }

    public Task<int> DeleteAsync(string connectionId, CancellationToken ct = default)
    {
        var list = ReadList();
        list.RemoveAll(c => c.Id == connectionId);
        WriteList(list);
        var activeId = Preferences.Default.Get<string?>(ActiveIdKey, null);
        if (activeId == connectionId)
        {
            if (list.Count > 0)
                Preferences.Default.Set(ActiveIdKey, list[0].Id);
            else
                Preferences.Default.Remove(ActiveIdKey);
        }
        return Task.FromResult(list.Count);
    }

    public Task SetActiveAsync(string connectionId, CancellationToken ct = default)
    {
        Preferences.Default.Set(ActiveIdKey, connectionId);
        return Task.CompletedTask;
    }

    public Task<string?> GetActiveIdAsync(CancellationToken ct = default)
    {
        var id = Preferences.Default.Get<string?>(ActiveIdKey, null);
        return Task.FromResult(id);
    }

    private List<ServerConnection> ReadList()
    {
        var query = NewQuery();
        var result = SecKeyChain.QueryAsData(query, false, out var status);
        if (status != SecStatusCode.Success || result is null) return new List<ServerConnection>();
        var json = NSString.FromData(result, NSStringEncoding.UTF8)?.ToString();
        if (string.IsNullOrEmpty(json)) return new List<ServerConnection>();
        return JsonSerializer.Deserialize<List<ServerConnection>>(json) ?? new List<ServerConnection>();
    }

    private void WriteList(List<ServerConnection> list)
    {
        var json = JsonSerializer.Serialize(list);
        var data = NSData.FromString(json);
        var query = NewQuery();
        var existing = SecKeyChain.QueryAsRecord(query, out _);
        if (existing is not null)
        {
            if (list.Count == 0)
                SecKeyChain.Remove(query);
            else
                SecKeyChain.Update(query, new SecRecord(SecKind.GenericPassword) { ValueData = data });
        }
        else if (list.Count > 0)
        {
            var record = NewQuery();
            record.ValueData = data;
            SecKeyChain.Add(record);
        }
    }

    private static SecRecord NewQuery() => new(SecKind.GenericPassword)
    {
        Service = Service,
        Account = Account
    };
}
```

- [ ] **Step 2: Commit**

```bash
git add src/app/OpenAgent.App/Platforms/iOS/IosKeychainConnectionStore.cs
git commit -m "feat(app): add IosKeychainConnectionStore backed by Keychain + Preferences"
```

---

### Task 4: Migrate ConversationCache to per-connection files

**Files:**
- Modify: `src/app/OpenAgent.App.Core/Services/ConversationCache.cs`
- Modify: `src/app/OpenAgent.App.Tests/Services/ConversationCacheTests.cs`

- [ ] **Step 1: Update the tests first**

Replace the entire test file:

```csharp
// src/app/OpenAgent.App.Tests/Services/ConversationCacheTests.cs
using OpenAgent.App.Core.Models;
using OpenAgent.App.Core.Services;

namespace OpenAgent.App.Tests.Services;

public class ConversationCacheTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "ccache_" + Guid.NewGuid().ToString("N"));

    public void Dispose() { try { Directory.Delete(_tmp, recursive: true); } catch { } }

    [Fact]
    public async Task Round_trips_list_for_connection()
    {
        var c = new ConversationCache(_tmp);
        var items = new List<ConversationListItem>
        {
            new() { Id = "a", Source = "app", CreatedAt = DateTimeOffset.UtcNow }
        };
        await c.WriteAsync("conn1", items);
        var got = await c.ReadAsync("conn1");
        Assert.Single(got!);
        Assert.Equal("a", got![0].Id);
    }

    [Fact]
    public async Task Read_when_missing_returns_null()
    {
        var c = new ConversationCache(_tmp);
        Assert.Null(await c.ReadAsync("conn1"));
    }

    [Fact]
    public async Task Read_corrupted_returns_null_and_does_not_throw()
    {
        Directory.CreateDirectory(_tmp);
        await File.WriteAllTextAsync(Path.Combine(_tmp, "conversations-conn1.cache.json"), "{not json");
        var c = new ConversationCache(_tmp);
        Assert.Null(await c.ReadAsync("conn1"));
    }

    [Fact]
    public async Task Different_connections_have_separate_caches()
    {
        var c = new ConversationCache(_tmp);
        var items1 = new List<ConversationListItem> { new() { Id = "a", Source = "app", CreatedAt = DateTimeOffset.UtcNow } };
        var items2 = new List<ConversationListItem> { new() { Id = "b", Source = "telegram", CreatedAt = DateTimeOffset.UtcNow } };
        await c.WriteAsync("conn1", items1);
        await c.WriteAsync("conn2", items2);
        var got1 = await c.ReadAsync("conn1");
        var got2 = await c.ReadAsync("conn2");
        Assert.Equal("a", got1![0].Id);
        Assert.Equal("b", got2![0].Id);
    }

    [Fact]
    public async Task DeleteCache_removes_file()
    {
        var c = new ConversationCache(_tmp);
        var items = new List<ConversationListItem> { new() { Id = "a", Source = "app", CreatedAt = DateTimeOffset.UtcNow } };
        await c.WriteAsync("conn1", items);
        c.DeleteCache("conn1");
        Assert.Null(await c.ReadAsync("conn1"));
    }

    [Fact]
    public void DeleteCache_does_not_throw_when_missing()
    {
        var c = new ConversationCache(_tmp);
        c.DeleteCache("nonexistent");
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `cd src/app && dotnet test --filter ConversationCacheTests`
Expected: build failure — `ReadAsync`/`WriteAsync` signatures don't match.

- [ ] **Step 3: Update ConversationCache**

Replace the entire file:

```csharp
// src/app/OpenAgent.App.Core/Services/ConversationCache.cs
using System.Text.Json;
using OpenAgent.App.Core.Models;

namespace OpenAgent.App.Core.Services;

/// <summary>Per-connection filesystem cache for conversation lists. Each connection gets its own file.</summary>
public sealed class ConversationCache
{
    private readonly string _baseDir;

    public ConversationCache(string baseDirectory) => _baseDir = baseDirectory;

    /// <summary>Reads the cached conversation list for a connection, or null if no cache exists.</summary>
    public async Task<List<ConversationListItem>?> ReadAsync(string connectionId, CancellationToken ct = default)
    {
        try
        {
            var path = PathFor(connectionId);
            if (!File.Exists(path)) return null;
            await using var stream = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync<List<ConversationListItem>>(stream, JsonOptions.Default, ct);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Writes the conversation list cache for a connection.</summary>
    public async Task WriteAsync(string connectionId, IReadOnlyList<ConversationListItem> items, CancellationToken ct = default)
    {
        var path = PathFor(connectionId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        await using (var stream = File.Create(tmp))
        {
            await JsonSerializer.SerializeAsync(stream, items, JsonOptions.Default, ct);
        }
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>Removes the cache file for a connection.</summary>
    public void DeleteCache(string connectionId)
    {
        var path = PathFor(connectionId);
        try { File.Delete(path); } catch { }
    }

    private string PathFor(string connectionId) =>
        Path.Combine(_baseDir, $"conversations-{connectionId}.cache.json");
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `cd src/app && dotnet test --filter ConversationCacheTests`
Expected: all 6 tests PASS.

- [ ] **Step 5: Commit**

```bash
git add src/app/OpenAgent.App.Core/Services/ConversationCache.cs src/app/OpenAgent.App.Tests/Services/ConversationCacheTests.cs
git commit -m "feat(app): make ConversationCache per-connection"
```

---

### Task 5: Migrate ApiClient and VoiceWebSocketClient to IConnectionStore

**Files:**
- Modify: `src/app/OpenAgent.App.Core/Api/ApiClient.cs`
- Modify: `src/app/OpenAgent.App.Core/Voice/VoiceWebSocketClient.cs`
- Modify: `src/app/OpenAgent.App.Tests/Api/ApiClientTests.cs`

- [ ] **Step 1: Update ApiClient to use IConnectionStore**

In `ApiClient.cs`, change the constructor and field from `ICredentialStore` to `IConnectionStore`, and change `LoadAsync` to `LoadActiveAsync`:

Replace:
```csharp
using OpenAgent.App.Core.Services;
```
(keep this, no change needed)

Replace constructor + field:
```csharp
    private readonly HttpClient _http;
    private readonly ICredentialStore _credentials;
    private readonly ILogger<ApiClient> _logger;

    /// <summary>Create the client with a shared <see cref="HttpClient"/> and a credential source.</summary>
    public ApiClient(HttpClient http, ICredentialStore credentials, ILogger<ApiClient>? logger = null)
    {
        _http = http;
        _credentials = credentials;
        _logger = logger ?? NullLogger<ApiClient>.Instance;
    }
```
with:
```csharp
    private readonly HttpClient _http;
    private readonly IConnectionStore _connections;
    private readonly ILogger<ApiClient> _logger;

    /// <summary>Create the client with a shared <see cref="HttpClient"/> and a connection store.</summary>
    public ApiClient(HttpClient http, IConnectionStore connections, ILogger<ApiClient>? logger = null)
    {
        _http = http;
        _connections = connections;
        _logger = logger ?? NullLogger<ApiClient>.Instance;
    }
```

In `SendAsync`, replace:
```csharp
        var creds = await _credentials.LoadAsync(ct) ?? throw new InvalidOperationException("No credentials");
        var req = new HttpRequestMessage(method, new Uri(new Uri(creds.BaseUrl), path));
        req.Headers.Add("X-Api-Key", creds.Token);
```
with:
```csharp
        var conn = await _connections.LoadActiveAsync(ct) ?? throw new InvalidOperationException("No active connection");
        var req = new HttpRequestMessage(method, new Uri(new Uri(conn.BaseUrl), path));
        req.Headers.Add("X-Api-Key", conn.Token);
```

- [ ] **Step 2: Update VoiceWebSocketClient to use IConnectionStore**

In `VoiceWebSocketClient.cs`, change the constructor and field:

Replace:
```csharp
    private readonly ICredentialStore _credentials;
    private readonly ILogger<VoiceWebSocketClient> _logger;
```
and constructor:
```csharp
    public VoiceWebSocketClient(ICredentialStore credentials, ILogger<VoiceWebSocketClient>? logger = null)
    {
        _credentials = credentials;
        _logger = logger ?? NullLogger<VoiceWebSocketClient>.Instance;
    }
```
with:
```csharp
    private readonly IConnectionStore _connections;
    private readonly ILogger<VoiceWebSocketClient> _logger;
```
and:
```csharp
    public VoiceWebSocketClient(IConnectionStore connections, ILogger<VoiceWebSocketClient>? logger = null)
    {
        _connections = connections;
        _logger = logger ?? NullLogger<VoiceWebSocketClient>.Instance;
    }
```

In `ConnectAsync`, replace:
```csharp
        var creds = await _credentials.LoadAsync(ct) ?? throw new InvalidOperationException("No credentials");
        var baseUri = new Uri(creds.BaseUrl);
        var scheme = baseUri.Scheme == "https" ? "wss" : "ws";
        var wsUrl = new UriBuilder($"{scheme}://{baseUri.Authority}{baseUri.AbsolutePath.TrimEnd('/')}/ws/conversations/{Uri.EscapeDataString(conversationId)}/voice")
        {
            Query = $"api_key={Uri.EscapeDataString(creds.Token)}"
        }.Uri;
```
with:
```csharp
        var conn = await _connections.LoadActiveAsync(ct) ?? throw new InvalidOperationException("No active connection");
        var baseUri = new Uri(conn.BaseUrl);
        var scheme = baseUri.Scheme == "https" ? "wss" : "ws";
        var wsUrl = new UriBuilder($"{scheme}://{baseUri.Authority}{baseUri.AbsolutePath.TrimEnd('/')}/ws/conversations/{Uri.EscapeDataString(conversationId)}/voice")
        {
            Query = $"api_key={Uri.EscapeDataString(conn.Token)}"
        }.Uri;
```

- [ ] **Step 3: Update ApiClientTests to use InMemoryConnectionStore**

In `ApiClientTests.cs`, change the `Make` method:

Replace:
```csharp
    private (ApiClient client, StubHandler stub) Make(
        Func<HttpRequestMessage, HttpResponseMessage>? respond = null)
    {
        var store = new InMemoryCredentialStore();
        store.SaveAsync(new QrPayload("https://agent.example/", "tok123")).GetAwaiter().GetResult();
        var stub = new StubHandler(respond ?? (_ => new HttpResponseMessage(HttpStatusCode.OK)));
        var http = new HttpClient(stub);
        return (new ApiClient(http, store), stub);
    }
```
with:
```csharp
    private (ApiClient client, StubHandler stub) Make(
        Func<HttpRequestMessage, HttpResponseMessage>? respond = null)
    {
        var store = new InMemoryConnectionStore();
        store.SaveAsync(new ServerConnection("c1", "Test", "https://agent.example/", "tok123")).GetAwaiter().GetResult();
        var stub = new StubHandler(respond ?? (_ => new HttpResponseMessage(HttpStatusCode.OK)));
        var http = new HttpClient(stub);
        return (new ApiClient(http, store), stub);
    }
```

Update the imports at the top — remove `using OpenAgent.App.Core.Onboarding;` (no longer needed).

- [ ] **Step 4: Run all tests to verify they pass**

Run: `cd src/app && dotnet test`
Expected: all tests PASS. (ConversationCacheTests already done, ApiClientTests updated.)

- [ ] **Step 5: Commit**

```bash
git add src/app/OpenAgent.App.Core/Api/ApiClient.cs src/app/OpenAgent.App.Core/Voice/VoiceWebSocketClient.cs src/app/OpenAgent.App.Tests/Api/ApiClientTests.cs
git commit -m "feat(app): migrate ApiClient and VoiceWebSocketClient to IConnectionStore"
```

---

### Task 6: Migrate OnboardingViewModel and ManualEntryViewModel

**Files:**
- Modify: `src/app/OpenAgent.App/ViewModels/OnboardingViewModel.cs`
- Modify: `src/app/OpenAgent.App/ViewModels/ManualEntryViewModel.cs`

Both view models currently save a `QrPayload` to `ICredentialStore`. They need to create a `ServerConnection` (new GUID, hostname as default name) and save it to `IConnectionStore`, then set it active.

- [ ] **Step 1: Update OnboardingViewModel**

Replace the entire file:

```csharp
// src/app/OpenAgent.App/ViewModels/OnboardingViewModel.cs
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OpenAgent.App.Core.Onboarding;
using OpenAgent.App.Core.Services;

namespace OpenAgent.App.ViewModels;

/// <summary>
/// View model for the QR-scan onboarding page. Parses scanned payloads via
/// <see cref="QrPayloadParser"/>, creates a <see cref="ServerConnection"/>,
/// and persists it to <see cref="IConnectionStore"/>.
/// </summary>
public partial class OnboardingViewModel : ObservableObject
{
    private readonly IConnectionStore _store;
    private readonly ILogger<OnboardingViewModel> _logger;

    /// <summary>When true, the QR scan was triggered from Settings (add connection) rather than first-time onboarding.</summary>
    [ObservableProperty] private bool _isAddMode;

    /// <summary>Creates a new view model bound to the supplied connection store.</summary>
    public OnboardingViewModel(IConnectionStore store, ILogger<OnboardingViewModel>? logger = null)
    {
        _store = store;
        _logger = logger ?? NullLogger<OnboardingViewModel>.Instance;
    }

    [ObservableProperty] private string? _error;
    [ObservableProperty] private bool _hasError;

    /// <summary>Handles a scanned QR payload: parses, creates connection, then navigates.</summary>
    [RelayCommand]
    public async Task OnQrScannedAsync(string text)
    {
        if (!QrPayloadParser.TryParse(text, out var payload, out var err))
        {
            _logger.LogWarning("QR parse failed (len={Len}): {Error}", text?.Length ?? 0, err);
            Error = err;
            HasError = true;
            return;
        }

        var uri = new Uri(payload!.BaseUrl);
        _logger.LogInformation("QR parsed ok host={Host}", uri.Host);

        var conn = new ServerConnection(
            Id: Guid.NewGuid().ToString(),
            Name: uri.Host,
            BaseUrl: payload.BaseUrl,
            Token: payload.Token);

        await _store.SaveAsync(conn);
        await _store.SetActiveAsync(conn.Id);

        if (IsAddMode)
            await Shell.Current.GoToAsync("..");
        else
            await Shell.Current.GoToAsync("//conversations");
    }

    /// <summary>Pushes the manual-entry page onto the navigation stack.</summary>
    [RelayCommand]
    public Task OpenManualEntryAsync() => Shell.Current.GoToAsync("manual-entry");
}
```

- [ ] **Step 2: Update ManualEntryViewModel**

Replace the entire file:

```csharp
// src/app/OpenAgent.App/ViewModels/ManualEntryViewModel.cs
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenAgent.App.Core.Onboarding;
using OpenAgent.App.Core.Services;

namespace OpenAgent.App.ViewModels;

/// <summary>
/// View model for the manual server-URL + token entry page. Constructs a probe URL
/// of the form <c>{ServerUrl}/?token={Token}</c> and routes it through
/// <see cref="QrPayloadParser"/> so validation rules match the QR path exactly.
/// </summary>
public partial class ManualEntryViewModel : ObservableObject
{
    private readonly IConnectionStore _store;

    /// <summary>Creates a new view model bound to the supplied connection store.</summary>
    public ManualEntryViewModel(IConnectionStore store) => _store = store;

    [ObservableProperty] private string _serverUrl = "";
    [ObservableProperty] private string _token = "";
    [ObservableProperty] private string? _error;
    [ObservableProperty] private bool _hasError;

    /// <summary>Validates and persists the entered credentials, then navigates to the conversations root.</summary>
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

        var uri = new Uri(payload!.BaseUrl);
        var conn = new ServerConnection(
            Id: Guid.NewGuid().ToString(),
            Name: uri.Host,
            BaseUrl: payload.BaseUrl,
            Token: payload.Token);

        await _store.SaveAsync(conn);
        await _store.SetActiveAsync(conn.Id);
        await Shell.Current.GoToAsync("//conversations");
    }
}
```

- [ ] **Step 3: Commit**

```bash
git add src/app/OpenAgent.App/ViewModels/OnboardingViewModel.cs src/app/OpenAgent.App/ViewModels/ManualEntryViewModel.cs
git commit -m "feat(app): migrate OnboardingViewModel and ManualEntryViewModel to IConnectionStore"
```

---

### Task 7: Rewrite SettingsViewModel with connections management

**Files:**
- Modify: `src/app/OpenAgent.App/ViewModels/SettingsViewModel.cs`
- Modify: `src/app/OpenAgent.App/Pages/SettingsPage.xaml`
- Modify: `src/app/OpenAgent.App/Pages/SettingsPage.xaml.cs`

- [ ] **Step 1: Rewrite SettingsViewModel**

Replace the entire file:

```csharp
// src/app/OpenAgent.App/ViewModels/SettingsViewModel.cs
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenAgent.App.Core.Services;

namespace OpenAgent.App.ViewModels;

/// <summary>
/// Settings page with connections management: lists all server connections with name + URL,
/// supports rename (tap), delete (swipe), and add (navigates to QR scan). Also shows app version.
/// </summary>
public partial class SettingsViewModel : ObservableObject
{
    private readonly IConnectionStore _store;
    private readonly ConversationCache _cache;

    /// <summary>All stored server connections.</summary>
    public ObservableCollection<ServerConnection> Connections { get; } = new();

    /// <summary>Id of the currently active connection, used for visual highlight.</summary>
    [ObservableProperty] private string? _activeConnectionId;

    /// <summary>Current MAUI app version.</summary>
    [ObservableProperty] private string _appVersion = AppInfo.Current.VersionString;

    /// <summary>Creates a new settings view-model.</summary>
    public SettingsViewModel(IConnectionStore store, ConversationCache cache)
    {
        _store = store;
        _cache = cache;
    }

    /// <summary>Loads all connections and the active Id.</summary>
    [RelayCommand]
    public async Task LoadAsync()
    {
        var all = await _store.LoadAllAsync();
        ActiveConnectionId = await _store.GetActiveIdAsync();
        Connections.Clear();
        foreach (var c in all) Connections.Add(c);
    }

    /// <summary>Prompts for a new name and renames the connection.</summary>
    [RelayCommand]
    public async Task RenameConnectionAsync(ServerConnection connection)
    {
        var name = await Shell.Current.DisplayPromptAsync("Rename connection", "Name", initialValue: connection.Name);
        if (string.IsNullOrWhiteSpace(name)) return;
        var updated = connection with { Name = name.Trim() };
        await _store.SaveAsync(updated);
        await LoadAsync();
    }

    /// <summary>Confirms and deletes a connection. If active, switches to next. If none left, goes to onboarding.</summary>
    [RelayCommand]
    public async Task DeleteConnectionAsync(ServerConnection connection)
    {
        var ok = await Shell.Current.DisplayAlert("Delete connection?",
            $"Delete \"{connection.Name}\"? This cannot be undone.", "Delete", "Cancel");
        if (!ok) return;

        var remaining = await _store.DeleteAsync(connection.Id);
        _cache.DeleteCache(connection.Id);

        if (remaining == 0)
        {
            await Shell.Current.GoToAsync("//onboarding");
            return;
        }

        await LoadAsync();

        if (connection.Id == ActiveConnectionId)
        {
            await Shell.Current.GoToAsync("//conversations");
        }
    }

    /// <summary>Navigates to the QR scan page in add-connection mode.</summary>
    [RelayCommand]
    public Task AddConnectionAsync() => Shell.Current.GoToAsync("onboarding-add");
}
```

- [ ] **Step 2: Rewrite SettingsPage.xaml**

Replace the entire file:

```xml
<?xml version="1.0" encoding="utf-8" ?>
<ContentPage xmlns="http://schemas.microsoft.com/dotnet/2021/maui"
             xmlns:x="http://schemas.microsoft.com/winfx/2009/xaml"
             xmlns:vm="clr-namespace:OpenAgent.App.ViewModels"
             xmlns:svc="clr-namespace:OpenAgent.App.Core.Services;assembly=OpenAgent.App.Core"
             x:Class="OpenAgent.App.Pages.SettingsPage"
             x:DataType="vm:SettingsViewModel"
             Title="Settings">
  <ScrollView>
    <VerticalStackLayout Padding="24" Spacing="20">

      <Label Text="Connections" FontAttributes="Bold" FontSize="18" />
      <CollectionView ItemsSource="{Binding Connections}" SelectionMode="None">
        <CollectionView.ItemTemplate>
          <DataTemplate x:DataType="svc:ServerConnection">
            <SwipeView>
              <SwipeView.RightItems>
                <SwipeItems>
                  <SwipeItem Text="Delete" BackgroundColor="Red"
                             Command="{Binding Source={RelativeSource AncestorType={x:Type vm:SettingsViewModel}}, Path=DeleteConnectionCommand}"
                             CommandParameter="{Binding .}" />
                </SwipeItems>
              </SwipeView.RightItems>
              <Grid Padding="16,12" ColumnDefinitions="*,Auto">
                <Grid.GestureRecognizers>
                  <TapGestureRecognizer Command="{Binding Source={RelativeSource AncestorType={x:Type vm:SettingsViewModel}}, Path=RenameConnectionCommand}"
                                        CommandParameter="{Binding .}" />
                </Grid.GestureRecognizers>
                <VerticalStackLayout Grid.Column="0" Spacing="2">
                  <Label Text="{Binding Name}" FontSize="16" />
                  <Label Text="{Binding BaseUrl}" FontSize="12" Opacity="0.6" />
                </VerticalStackLayout>
                <Label Grid.Column="1" Text="Active" FontSize="12" TextColor="#0A84FF" VerticalOptions="Center"
                       IsVisible="{Binding Id, Converter={StaticResource IsActiveConverter}}" />
              </Grid>
            </SwipeView>
          </DataTemplate>
        </CollectionView.ItemTemplate>
        <CollectionView.EmptyView>
          <Label Text="No connections" Opacity="0.6" HorizontalOptions="Center" />
        </CollectionView.EmptyView>
      </CollectionView>

      <Button Text="Add connection" Command="{Binding AddConnectionCommand}" />

      <Label Text="App version" FontAttributes="Bold" Margin="0,12,0,0" />
      <Label Text="{Binding AppVersion}" />

    </VerticalStackLayout>
  </ScrollView>
</ContentPage>
```

- [ ] **Step 3: Update SettingsPage.xaml.cs to provide the IsActiveConverter**

Replace the entire file:

```csharp
// src/app/OpenAgent.App/Pages/SettingsPage.xaml.cs
using System.Globalization;
using OpenAgent.App.ViewModels;

namespace OpenAgent.App.Pages;

/// <summary>Settings page with connections management.</summary>
public partial class SettingsPage : ContentPage
{
    private readonly SettingsViewModel _vm;

    public SettingsPage(SettingsViewModel vm)
    {
        InitializeComponent();
        BindingContext = _vm = vm;
        Resources.Add("IsActiveConverter", new IsActiveConnectionConverter(_vm));
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await _vm.LoadCommand.ExecuteAsync(null);
    }
}

/// <summary>Returns true when the bound connection Id matches the active connection Id on the view model.</summary>
internal sealed class IsActiveConnectionConverter : IValueConverter
{
    private readonly SettingsViewModel _vm;
    public IsActiveConnectionConverter(SettingsViewModel vm) => _vm = vm;

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is string id && id == _vm.ActiveConnectionId;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
```

- [ ] **Step 4: Commit**

```bash
git add src/app/OpenAgent.App/ViewModels/SettingsViewModel.cs src/app/OpenAgent.App/Pages/SettingsPage.xaml src/app/OpenAgent.App/Pages/SettingsPage.xaml.cs
git commit -m "feat(app): rewrite Settings with connections list (rename, delete, add)"
```

---

### Task 8: Migrate ConversationsViewModel + top-bar connection picker

**Files:**
- Modify: `src/app/OpenAgent.App/ViewModels/ConversationsViewModel.cs`
- Modify: `src/app/OpenAgent.App/Pages/ConversationsPage.xaml`
- Modify: `src/app/OpenAgent.App/Pages/ConversationsPage.xaml.cs`

- [ ] **Step 1: Update ConversationsViewModel**

The VM needs `IConnectionStore` to know the active connection (for cache key and for the picker). Add a `Connections` collection and `SelectedConnection` for the top bar picker.

Replace the entire file:

```csharp
// src/app/OpenAgent.App/ViewModels/ConversationsViewModel.cs
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenAgent.App.Core.Api;
using OpenAgent.App.Core.Models;
using OpenAgent.App.Core.Services;

namespace OpenAgent.App.ViewModels;

/// <summary>
/// Conversations list with a top-bar connection picker. Loads conversations for the active
/// connection, supports switching via the picker, and provides swipe actions.
/// </summary>
public partial class ConversationsViewModel : ObservableObject
{
    private readonly IApiClient _api;
    private readonly IConnectionStore _connectionStore;
    private readonly ConversationCache _cache;
    private bool _suppressPickerChange;

    /// <summary>Conversation rows bound to the CollectionView.</summary>
    public ObservableCollection<ConversationListItem> Items { get; } = new();

    /// <summary>Available connections for the top-bar picker.</summary>
    public ObservableCollection<ServerConnection> Connections { get; } = new();

    /// <summary>Currently selected connection in the picker.</summary>
    [ObservableProperty] private ServerConnection? _selectedConnection;

    [ObservableProperty] private bool _isOffline;
    [ObservableProperty] private bool _isRefreshing;

    public ConversationsViewModel(IApiClient api, IConnectionStore connectionStore, ConversationCache cache)
    {
        _api = api;
        _connectionStore = connectionStore;
        _cache = cache;
    }

    /// <summary>Loads connections into the picker and refreshes conversations for the active one.</summary>
    [RelayCommand]
    public async Task LoadAsync()
    {
        IsRefreshing = true;

        var all = await _connectionStore.LoadAllAsync();
        var active = await _connectionStore.LoadActiveAsync();

        _suppressPickerChange = true;
        Connections.Clear();
        foreach (var c in all) Connections.Add(c);
        SelectedConnection = active is not null ? Connections.FirstOrDefault(c => c.Id == active.Id) : Connections.FirstOrDefault();
        _suppressPickerChange = false;

        if (active is null)
        {
            IsRefreshing = false;
            return;
        }

        await RefreshConversationsAsync(active.Id);
        IsRefreshing = false;
    }

    /// <summary>Called when the picker selection changes. Switches active connection and reloads.</summary>
    partial void OnSelectedConnectionChanged(ServerConnection? value)
    {
        if (_suppressPickerChange || value is null) return;
        _ = SwitchConnectionAsync(value);
    }

    private async Task SwitchConnectionAsync(ServerConnection connection)
    {
        await _connectionStore.SetActiveAsync(connection.Id);
        IsRefreshing = true;
        await RefreshConversationsAsync(connection.Id);
        IsRefreshing = false;
    }

    private async Task RefreshConversationsAsync(string connectionId)
    {
        var cached = await _cache.ReadAsync(connectionId);
        if (cached is not null) Replace(cached);

        try
        {
            var fresh = await _api.GetConversationsAsync();
            await _cache.WriteAsync(connectionId, fresh);
            Replace(fresh);
            IsOffline = false;
        }
        catch (AuthRejectedException)
        {
            await Shell.Current.DisplayAlert("Authentication failed",
                "The agent rejected the API token. Please reconfigure.", "OK");
            await Shell.Current.GoToAsync("settings");
        }
        catch
        {
            IsOffline = cached is not null;
            if (cached is null)
                await Shell.Current.DisplayAlert("Offline", "Couldn't reach agent.", "OK");
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

- [ ] **Step 2: Update ConversationsPage.xaml with connection picker in toolbar**

Replace the entire file:

```xml
<?xml version="1.0" encoding="utf-8" ?>
<ContentPage xmlns="http://schemas.microsoft.com/dotnet/2021/maui"
             xmlns:x="http://schemas.microsoft.com/winfx/2009/xaml"
             xmlns:vm="clr-namespace:OpenAgent.App.ViewModels"
             xmlns:m="clr-namespace:OpenAgent.App.Core.Models;assembly=OpenAgent.App.Core"
             xmlns:svc="clr-namespace:OpenAgent.App.Core.Services;assembly=OpenAgent.App.Core"
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

  <Grid RowDefinitions="Auto,*">
    <!-- Connection picker -->
    <Picker Grid.Row="0" Margin="16,8"
            ItemsSource="{Binding Connections}"
            SelectedItem="{Binding SelectedConnection}"
            ItemDisplayBinding="{Binding Name}"
            Title="Connection" />

    <RefreshView Grid.Row="1" IsRefreshing="{Binding IsRefreshing}" Command="{Binding LoadCommand}">
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

    <Button Grid.Row="1" Text="+" Command="{Binding NewCallCommand}"
            BackgroundColor="#0A84FF" TextColor="White"
            CornerRadius="28" WidthRequest="56" HeightRequest="56"
            HorizontalOptions="End" VerticalOptions="End" Margin="0,0,24,24" />
  </Grid>
</ContentPage>
```

- [ ] **Step 3: ConversationsPage.xaml.cs stays the same** — no changes needed.

- [ ] **Step 4: Commit**

```bash
git add src/app/OpenAgent.App/ViewModels/ConversationsViewModel.cs src/app/OpenAgent.App/Pages/ConversationsPage.xaml src/app/OpenAgent.App/Pages/ConversationsPage.xaml.cs
git commit -m "feat(app): add connection picker to conversations page"
```

---

### Task 9: Wire DI, AppShell routing, register onboarding-add route

**Files:**
- Modify: `src/app/OpenAgent.App/MauiProgram.cs`
- Modify: `src/app/OpenAgent.App/AppShell.xaml.cs`
- Modify: `src/app/OpenAgent.App/Pages/OnboardingPage.xaml.cs`

- [ ] **Step 1: Update MauiProgram.cs**

Replace `ICredentialStore` registration with `IConnectionStore`:

Replace:
```csharp
        builder.Services.AddSingleton<ICredentialStore, IosKeychainCredentialStore>();
```
with:
```csharp
        builder.Services.AddSingleton<IConnectionStore, IosKeychainConnectionStore>();
```

- [ ] **Step 2: Update AppShell.xaml.cs**

Replace the entire file:

```csharp
// src/app/OpenAgent.App/AppShell.xaml.cs
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
        Routing.RegisterRoute("onboarding-add", typeof(Pages.OnboardingPage));
    }

    /// <summary>Routes the user to onboarding or conversations based on stored connections.</summary>
    public async Task RouteInitialAsync()
    {
        var services = IPlatformApplication.Current?.Services
                       ?? throw new InvalidOperationException("Service provider not available");
        var store = services.GetRequiredService<IConnectionStore>();
        var active = await store.LoadActiveAsync();
        await GoToAsync(active is null ? "//onboarding" : "//conversations");
    }
}
```

- [ ] **Step 3: Update OnboardingPage.xaml.cs to set IsAddMode from route**

Replace the entire file:

```csharp
// src/app/OpenAgent.App/Pages/OnboardingPage.xaml.cs
using ZXing.Net.Maui;
using OpenAgent.App.ViewModels;

namespace OpenAgent.App.Pages;

/// <summary>
/// QR-scan page. Used for first-time onboarding (navigated via //onboarding) and for adding
/// connections (navigated via "onboarding-add" from settings). The view model's IsAddMode
/// flag controls post-scan navigation.
/// </summary>
[QueryProperty(nameof(IsAddMode), "isAddMode")]
public partial class OnboardingPage : ContentPage
{
    private readonly OnboardingViewModel _vm;
    private bool _handled;

    public string? IsAddMode { get; set; }

    public OnboardingPage(OnboardingViewModel vm)
    {
        InitializeComponent();
        BindingContext = _vm = vm;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        _handled = false;
        _vm.IsAddMode = string.Equals(IsAddMode, "true", StringComparison.OrdinalIgnoreCase);
    }

    private async void OnBarcodesDetected(object? sender, BarcodeDetectionEventArgs e)
    {
        if (_handled) return;
        var text = e.Results?.FirstOrDefault()?.Value;
        if (string.IsNullOrEmpty(text)) return;
        _handled = true;
        await MainThread.InvokeOnMainThreadAsync(() => _vm.OnQrScannedAsync(text));
    }
}
```

- [ ] **Step 4: Update the AddConnection navigation in SettingsViewModel**

In `SettingsViewModel.cs`, update the `AddConnectionAsync` method to pass the add-mode flag:

Replace:
```csharp
    public Task AddConnectionAsync() => Shell.Current.GoToAsync("onboarding-add");
```
with:
```csharp
    public Task AddConnectionAsync() => Shell.Current.GoToAsync("onboarding-add?isAddMode=true");
```

- [ ] **Step 5: Commit**

```bash
git add src/app/OpenAgent.App/MauiProgram.cs src/app/OpenAgent.App/AppShell.xaml.cs src/app/OpenAgent.App/Pages/OnboardingPage.xaml.cs src/app/OpenAgent.App/ViewModels/SettingsViewModel.cs
git commit -m "feat(app): wire IConnectionStore DI, add onboarding-add route, set IsAddMode"
```

---

### Task 10: Remove old ICredentialStore and IosKeychainCredentialStore

**Files:**
- Delete: `src/app/OpenAgent.App.Core/Services/ICredentialStore.cs`
- Delete: `src/app/OpenAgent.App.Core/Services/InMemoryCredentialStore.cs`
- Delete: `src/app/OpenAgent.App/Platforms/iOS/IosKeychainCredentialStore.cs`
- Delete: `src/app/OpenAgent.App.Tests/Services/InMemoryCredentialStoreTests.cs`
- Modify: `src/app/OpenAgent.App.Core/Logging/AgentLoggerProvider.cs` (if it references ICredentialStore)

- [ ] **Step 1: Check AgentLoggerProvider for ICredentialStore references**

Read `AgentLoggerProvider.cs` — it uses `IApiClient` not `ICredentialStore` directly, so no change needed.

- [ ] **Step 2: Delete old files**

```bash
git rm src/app/OpenAgent.App.Core/Services/ICredentialStore.cs
git rm src/app/OpenAgent.App.Core/Services/InMemoryCredentialStore.cs
git rm src/app/OpenAgent.App/Platforms/iOS/IosKeychainCredentialStore.cs
git rm src/app/OpenAgent.App.Tests/Services/InMemoryCredentialStoreTests.cs
```

- [ ] **Step 3: Build to verify no remaining references**

Run: `cd src/app && dotnet build`
Expected: build succeeds with no errors.

- [ ] **Step 4: Run all tests**

Run: `cd src/app && dotnet test`
Expected: all tests PASS.

- [ ] **Step 5: Commit**

```bash
git commit -m "chore(app): remove old ICredentialStore and single-connection implementations"
```

---

### Task 11: Keychain migration — import existing single connection

**Files:**
- Modify: `src/app/OpenAgent.App/Platforms/iOS/IosKeychainConnectionStore.cs`

Users who already have the app have a `QrPayload` stored under `Service=OpenAgent, Account=default`. On first launch after the update, the new store should detect and migrate it.

- [ ] **Step 1: Add migration to IosKeychainConnectionStore**

Add a private method `MigrateFromLegacy` that checks for the old Keychain entry, reads it, creates a `ServerConnection`, saves it to the new format, and deletes the old entry. Call it at the top of `ReadList()` the first time.

Add a field and method to the class:

```csharp
    private bool _migrationChecked;

    private void MigrateFromLegacyIfNeeded()
    {
        if (_migrationChecked) return;
        _migrationChecked = true;

        var legacyQuery = new SecRecord(SecKind.GenericPassword) { Service = Service, Account = "default" };
        var result = SecKeyChain.QueryAsData(legacyQuery, false, out var status);
        if (status != SecStatusCode.Success || result is null) return;

        var json = NSString.FromData(result, NSStringEncoding.UTF8)?.ToString();
        if (string.IsNullOrEmpty(json)) return;

        try
        {
            var legacy = JsonSerializer.Deserialize<LegacyQrPayload>(json);
            if (legacy?.BaseUrl is null || legacy.Token is null) return;

            var uri = new Uri(legacy.BaseUrl);
            var conn = new ServerConnection(Guid.NewGuid().ToString(), uri.Host, legacy.BaseUrl, legacy.Token);
            var list = new List<ServerConnection> { conn };
            WriteList(list);
            Preferences.Default.Set(ActiveIdKey, conn.Id);
            SecKeyChain.Remove(legacyQuery);
        }
        catch { }
    }

    private sealed record LegacyQrPayload(
        [property: JsonPropertyName("BaseUrl")] string? BaseUrl,
        [property: JsonPropertyName("Token")] string? Token);
```

Call `MigrateFromLegacyIfNeeded()` at the top of `ReadList()`:

```csharp
    private List<ServerConnection> ReadList()
    {
        MigrateFromLegacyIfNeeded();
        var query = NewQuery();
        // ... rest unchanged
    }
```

- [ ] **Step 2: Commit**

```bash
git add src/app/OpenAgent.App/Platforms/iOS/IosKeychainConnectionStore.cs
git commit -m "feat(app): migrate legacy single QrPayload Keychain entry to multi-connection format"
```

---

### Task 12: Final build + test pass

- [ ] **Step 1: Build all projects**

Run: `cd src/app && dotnet build`
Expected: build succeeds.

- [ ] **Step 2: Run all tests**

Run: `cd src/app && dotnet test`
Expected: all tests PASS.

- [ ] **Step 3: Verify no old ICredentialStore references remain**

Run: `grep -r "ICredentialStore" src/app/`
Expected: no matches.

Run: `grep -r "QrPayload" src/app/ --include="*.cs" | grep -v "QrPayloadParser" | grep -v "QrPayloadParserTests" | grep -v "QrPayload.cs" | grep -v "LegacyQrPayload"`
Expected: no matches (QrPayload record and parser are still used for parsing QR codes, but nothing stores it directly).
