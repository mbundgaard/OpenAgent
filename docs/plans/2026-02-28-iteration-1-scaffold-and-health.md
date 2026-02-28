# Iteration 1: Project Scaffold, Health Endpoint & Dockerfile

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** A running ASP.NET Core API with a health endpoint, integration test, and Dockerfile — the foundation everything else builds on.

**Architecture:** Minimal API (`web` template) with a single GET /health endpoint. Integration tested via `WebApplicationFactory`. Containerized with a multi-stage Dockerfile. Central package management from day one.

**Tech Stack:** .NET 10, ASP.NET Core Minimal API, xUnit, `Microsoft.AspNetCore.Mvc.Testing`, Docker

**Reference:** Design doc at `docs/startup-and-api-design.md`

---

### Task 1: Create solution and projects

**Files:**
- Create: `OpenAgent3.sln` (will be renamed to `.slnx` format)
- Create: `src/OpenAgent3.Api/OpenAgent3.Api.csproj`
- Create: `tests/OpenAgent3.Api.Tests/OpenAgent3.Api.Tests.csproj`
- Create: `Directory.Build.props`
- Create: `Directory.Packages.props`

**Step 1: Create the solution and API project**

```bash
cd C:/Users/martin/source/repos/OpenAgent3
dotnet new web -n OpenAgent3.Api -o src/OpenAgent3.Api --no-https
dotnet new sln -n OpenAgent3
dotnet sln add src/OpenAgent3.Api
```

This creates a minimal empty web project — no controllers, no swagger, just `Program.cs` with `app.Run()`.

**Step 2: Create the test project**

```bash
dotnet new xunit -n OpenAgent3.Api.Tests -o tests/OpenAgent3.Api.Tests
dotnet sln add tests/OpenAgent3.Api.Tests
dotnet add tests/OpenAgent3.Api.Tests reference src/OpenAgent3.Api
dotnet add tests/OpenAgent3.Api.Tests package Microsoft.AspNetCore.Mvc.Testing
```

**Step 3: Set up Central Package Management**

Create `Directory.Packages.props` in the repo root:

```xml
<Project>
  <PropertyGroup>
    <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
  </PropertyGroup>
  <ItemGroup>
    <PackageVersion Include="Microsoft.AspNetCore.Mvc.Testing" Version="10.0.0" />
    <PackageVersion Include="Microsoft.NET.Test.Sdk" Version="17.13.0" />
    <PackageVersion Include="xunit" Version="2.9.3" />
    <PackageVersion Include="xunit.runner.visualstudio" Version="3.0.2" />
  </ItemGroup>
</Project>
```

Create `Directory.Build.props` in the repo root:

```xml
<Project>
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
</Project>
```

Then remove `<TargetFramework>`, `<Nullable>`, and `<ImplicitUsings>` from both `.csproj` files since they're now centralized.

Remove `Version="..."` attributes from package references in the test `.csproj` since versions are now in `Directory.Packages.props`.

**Step 4: Verify it builds and tests pass**

```bash
dotnet build
dotnet test
```

Expected: Build succeeds. The default xUnit template test passes (1 passed).

**Step 5: Commit**

```bash
git init
git add -A
git commit -m "chore: scaffold solution with API and test projects"
```

---

### Task 2: Health endpoint

**Files:**
- Modify: `src/OpenAgent3.Api/Program.cs`

**Step 1: Write the health endpoint**

Replace `src/OpenAgent3.Api/Program.cs` with:

```csharp
var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

app.MapGet("/health", () => Results.Ok());

app.Run();
```

**Step 2: Verify it runs**

```bash
dotnet run --project src/OpenAgent3.Api &
curl http://localhost:5000/health
# Should return 200 OK
```

Kill the process after verifying.

**Step 3: Commit**

```bash
git add src/OpenAgent3.Api/Program.cs
git commit -m "feat: add health endpoint"
```

---

### Task 3: Integration test for health endpoint

**Files:**
- Modify: `tests/OpenAgent3.Api.Tests/UnitTest1.cs` → rename to `HealthEndpointTests.cs`

**Step 1: Write the failing test**

Delete `UnitTest1.cs` and create `HealthEndpointTests.cs`:

```csharp
using Microsoft.AspNetCore.Mvc.Testing;

namespace OpenAgent3.Api.Tests;

public class HealthEndpointTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient _client;

    public HealthEndpointTests(WebApplicationFactory<Program> factory)
    {
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task Health_ReturnsOk()
    {
        var response = await _client.GetAsync("/health");

        response.EnsureSuccessStatusCode();
    }
}
```

**Step 2: Make Program class accessible to test project**

The `WebApplicationFactory<Program>` needs access to the `Program` class. Add to `src/OpenAgent3.Api/Program.cs` at the bottom:

```csharp
// Make Program accessible to integration tests
public partial class Program;
```

**Step 3: Run the test**

```bash
dotnet test --verbosity normal
```

Expected: `HealthEndpointTests.Health_ReturnsOk` PASSED.

**Step 4: Commit**

```bash
git add -A
git commit -m "test: add integration test for health endpoint"
```

---

### Task 4: Dockerfile

**Files:**
- Create: `Dockerfile`
- Create: `.dockerignore`

**Step 1: Create `.dockerignore`**

```
**/.git
**/bin
**/obj
**/node_modules
**/.vs
**/docs
**/*.md
```

**Step 2: Create multi-stage `Dockerfile`**

```dockerfile
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy solution and project files first for layer caching
COPY *.sln Directory.Build.props Directory.Packages.props ./
COPY src/OpenAgent3.Api/OpenAgent3.Api.csproj src/OpenAgent3.Api/
COPY tests/OpenAgent3.Api.Tests/OpenAgent3.Api.Tests.csproj tests/OpenAgent3.Api.Tests/
RUN dotnet restore

# Copy everything and build
COPY . .
RUN dotnet build -c Release --no-restore

# Run tests inside the build
RUN dotnet test -c Release --no-build --no-restore

# Publish
FROM build AS publish
RUN dotnet publish src/OpenAgent3.Api -c Release --no-build -o /app/publish

# Runtime image
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=publish /app/publish .
EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080
ENTRYPOINT ["dotnet", "OpenAgent3.Api.dll"]
```

**Step 3: Build the Docker image**

```bash
docker build -t openagent3 .
```

Expected: Build succeeds, tests pass inside the container.

**Step 4: Run and verify**

```bash
docker run -d -p 8080:8080 --name openagent3-test openagent3
curl http://localhost:8080/health
# Should return 200 OK
docker stop openagent3-test && docker rm openagent3-test
```

**Step 5: Commit**

```bash
git add Dockerfile .dockerignore
git commit -m "feat: add Dockerfile with multi-stage build and test"
```

---

### Task 5: REST conversation CRUD

**Files:**
- Create: `src/OpenAgent3.Api/Conversations/Conversation.cs`
- Create: `src/OpenAgent3.Api/Conversations/ConversationStore.cs`
- Create: `src/OpenAgent3.Api/Conversations/ConversationEndpoints.cs`
- Modify: `src/OpenAgent3.Api/Program.cs`
- Create: `tests/OpenAgent3.Api.Tests/ConversationEndpointTests.cs`

**Step 1: Write the failing tests**

Create `tests/OpenAgent3.Api.Tests/ConversationEndpointTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace OpenAgent3.Api.Tests;

public class ConversationEndpointTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient _client;

    public ConversationEndpointTests(WebApplicationFactory<Program> factory)
    {
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task CreateConversation_ReturnsId()
    {
        var response = await _client.PostAsync("/api/conversations", null);

        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<ConversationResponse>();
        Assert.NotNull(body);
        Assert.False(string.IsNullOrEmpty(body.Id));
    }

    [Fact]
    public async Task GetConversation_AfterCreate_ReturnsIt()
    {
        var createResponse = await _client.PostAsync("/api/conversations", null);
        var created = await createResponse.Content.ReadFromJsonAsync<ConversationResponse>();

        var response = await _client.GetAsync($"/api/conversations/{created!.Id}");

        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<ConversationResponse>();
        Assert.Equal(created.Id, body!.Id);
    }

    [Fact]
    public async Task GetConversation_NotFound_Returns404()
    {
        var response = await _client.GetAsync("/api/conversations/does-not-exist");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task DeleteConversation_ReturnsNoContent()
    {
        var createResponse = await _client.PostAsync("/api/conversations", null);
        var created = await createResponse.Content.ReadFromJsonAsync<ConversationResponse>();

        var response = await _client.DeleteAsync($"/api/conversations/{created!.Id}");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        var getResponse = await _client.GetAsync($"/api/conversations/{created.Id}");
        Assert.Equal(HttpStatusCode.NotFound, getResponse.StatusCode);
    }

    private record ConversationResponse(string Id);
}
```

**Step 2: Run tests to verify they fail**

```bash
dotnet test --verbosity normal
```

Expected: 4 new tests FAIL (404 on POST, types not found, etc.). Health test still passes.

**Step 3: Implement the conversation model**

Create `src/OpenAgent3.Api/Conversations/Conversation.cs`:

```csharp
namespace OpenAgent3.Api.Conversations;

public sealed class Conversation
{
    public required string Id { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public string? VoiceSessionId { get; set; }
}
```

**Step 4: Implement the in-memory store**

Create `src/OpenAgent3.Api/Conversations/ConversationStore.cs`:

```csharp
using System.Collections.Concurrent;

namespace OpenAgent3.Api.Conversations;

public sealed class ConversationStore
{
    private readonly ConcurrentDictionary<string, Conversation> _conversations = new();

    public Conversation Create()
    {
        var conversation = new Conversation { Id = Ulid.NewUlid().ToString() };
        _conversations[conversation.Id] = conversation;
        return conversation;
    }

    public Conversation? Get(string id) =>
        _conversations.GetValueOrDefault(id);

    public bool Delete(string id) =>
        _conversations.TryRemove(id, out _);
}
```

**Step 5: Implement the endpoints**

Create `src/OpenAgent3.Api/Conversations/ConversationEndpoints.cs`:

```csharp
namespace OpenAgent3.Api.Conversations;

public static class ConversationEndpoints
{
    public static void MapConversationEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/conversations");

        group.MapPost("/", (ConversationStore store) =>
        {
            var conversation = store.Create();
            return Results.Ok(new { conversation.Id });
        });

        group.MapGet("/{id}", (string id, ConversationStore store) =>
        {
            var conversation = store.Get(id);
            return conversation is null
                ? Results.NotFound()
                : Results.Ok(new { conversation.Id });
        });

        group.MapDelete("/{id}", (string id, ConversationStore store) =>
        {
            store.Delete(id);
            return Results.NoContent();
        });
    }
}
```

**Step 6: Wire it into Program.cs**

Update `src/OpenAgent3.Api/Program.cs`:

```csharp
using OpenAgent3.Api.Conversations;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<ConversationStore>();

var app = builder.Build();

app.MapGet("/health", () => Results.Ok());
app.MapConversationEndpoints();

app.Run();

// Make Program accessible to integration tests
public partial class Program;
```

**Step 7: Run tests**

```bash
dotnet test --verbosity normal
```

Expected: All 5 tests pass (1 health + 4 conversation).

**Step 8: Commit**

```bash
git add -A
git commit -m "feat: add conversation CRUD endpoints with in-memory store"
```

---

### Task 6: WebSocket accept and echo

**Files:**
- Create: `src/OpenAgent3.Api/WebSockets/WebSocketEndpoints.cs`
- Modify: `src/OpenAgent3.Api/Program.cs`
- Create: `tests/OpenAgent3.Api.Tests/WebSocketEchoTests.cs`

**Step 1: Write the failing test**

Create `tests/OpenAgent3.Api.Tests/WebSocketEchoTests.cs`:

```csharp
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace OpenAgent3.Api.Tests;

public class WebSocketEchoTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public WebSocketEchoTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task WebSocket_SendMessage_EchoesBack()
    {
        var client = _factory.Server.CreateWebSocketClient();
        var ws = await client.ConnectAsync(
            new Uri(_factory.Server.BaseAddress, "/ws"), CancellationToken.None);

        var message = new { type = "text_message", conversationId = "test-123", text = "hello" };
        var json = JsonSerializer.Serialize(message);
        var bytes = Encoding.UTF8.GetBytes(json);

        await ws.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);

        var buffer = new byte[4096];
        var result = await ws.ReceiveAsync(buffer, CancellationToken.None);

        Assert.Equal(WebSocketMessageType.Text, result.MessageType);
        var echoJson = Encoding.UTF8.GetString(buffer, 0, result.Count);
        var echo = JsonDocument.Parse(echoJson);
        Assert.Equal("text_message", echo.RootElement.GetProperty("type").GetString());

        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
    }
}
```

**Step 2: Run test to verify it fails**

```bash
dotnet test --verbosity normal
```

Expected: WebSocket test FAILS (no WebSocket endpoint yet).

**Step 3: Implement WebSocket endpoint**

Create `src/OpenAgent3.Api/WebSockets/WebSocketEndpoints.cs`:

```csharp
using System.Net.WebSockets;

namespace OpenAgent3.Api.WebSockets;

public static class WebSocketEndpoints
{
    public static void MapWebSocketEndpoints(this WebApplication app)
    {
        app.Map("/ws", async (HttpContext context) =>
        {
            if (!context.WebSockets.IsWebSocketRequest)
            {
                context.Response.StatusCode = 400;
                return;
            }

            var ws = await context.WebSockets.AcceptWebSocketAsync();
            await EchoLoop(ws, context.RequestAborted);
        });
    }

    private static async Task EchoLoop(WebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[4096];

        while (ws.State == WebSocketState.Open)
        {
            var result = await ws.ReceiveAsync(buffer, ct);

            if (result.MessageType == WebSocketMessageType.Close)
            {
                await ws.CloseAsync(
                    WebSocketCloseStatus.NormalClosure, null, ct);
                break;
            }

            await ws.SendAsync(
                buffer.AsMemory(0, result.Count),
                result.MessageType,
                result.EndOfMessage,
                ct);
        }
    }
}
```

**Step 4: Wire into Program.cs**

Update `src/OpenAgent3.Api/Program.cs`:

```csharp
using OpenAgent3.Api.Conversations;
using OpenAgent3.Api.WebSockets;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<ConversationStore>();

var app = builder.Build();

app.UseWebSockets();

app.MapGet("/health", () => Results.Ok());
app.MapConversationEndpoints();
app.MapWebSocketEndpoints();

app.Run();

// Make Program accessible to integration tests
public partial class Program;
```

**Step 5: Run tests**

```bash
dotnet test --verbosity normal
```

Expected: All 6 tests pass.

**Step 6: Commit**

```bash
git add -A
git commit -m "feat: add WebSocket endpoint with echo loop"
```

---

## What Comes Next (not in this plan)

After this iteration is solid:
- **Iteration 2:** Outbound channel + writer loop, message dispatching by type, text handler with stubbed LLM
- **Iteration 3:** OpenAI Realtime client, audio path, voice session lifecycle via REST
