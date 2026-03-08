# API Key Authentication Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Add API key authentication to all endpoints via a pluggable `OpenAgent.Security.ApiKey` project, designed so it can be swapped for Entra ID later by replacing one extension method call.

**Architecture:** A new class library provides `AddApiKeyAuth()` extension on `IServiceCollection` that registers ASP.NET Core's authentication/authorization with a custom API key scheme. A handler reads `X-Api-Key` from the request header and validates against a configured key. All endpoints get `.RequireAuthorization()` except `/health`. Tests verify both happy path and 401 rejection.

**Tech Stack:** ASP.NET Core Authentication, custom `AuthenticationHandler<T>`, xUnit + WebApplicationFactory

---

### Task 1: Create the project and wire it into the solution

**Files:**
- Create: `src/agent/OpenAgent.Security.ApiKey/OpenAgent.Security.ApiKey.csproj`
- Modify: `src/agent/OpenAgent.sln`
- Modify: `src/agent/OpenAgent/OpenAgent.csproj`

**Step 1: Create the project**

```bash
cd src/agent && dotnet new classlib -n OpenAgent.Security.ApiKey
```

This creates the project with a default `Class1.cs`. Delete the placeholder file:

```bash
rm src/agent/OpenAgent.Security.ApiKey/Class1.cs
```

**Step 2: Add FrameworkReference for ASP.NET Core auth types**

Replace the generated csproj with:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <ItemGroup>
    <FrameworkReference Include="Microsoft.AspNetCore.App" />
  </ItemGroup>

</Project>
```

**Step 3: Add the project to the solution under a "Security" folder**

```bash
cd src/agent && dotnet sln add OpenAgent.Security.ApiKey/OpenAgent.Security.ApiKey.csproj --solution-folder Security
```

**Step 4: Add a project reference from the host**

```bash
cd src/agent && dotnet add OpenAgent/OpenAgent.csproj reference OpenAgent.Security.ApiKey/OpenAgent.Security.ApiKey.csproj
```

**Step 5: Verify it builds**

```bash
cd src/agent && dotnet build
```

Expected: Build succeeded.

**Step 6: Commit**

```
feat: add OpenAgent.Security.ApiKey project scaffold
```

---

### Task 2: Implement the API key authentication handler

**Files:**
- Create: `src/agent/OpenAgent.Security.ApiKey/ApiKeyAuthenticationHandler.cs`
- Create: `src/agent/OpenAgent.Security.ApiKey/ApiKeyAuthenticationOptions.cs`

**Step 1: Create the options class**

```csharp
// src/agent/OpenAgent.Security.ApiKey/ApiKeyAuthenticationOptions.cs
using Microsoft.AspNetCore.Authentication;

namespace OpenAgent.Security.ApiKey;

/// <summary>
/// Options for API key authentication — holds the expected key value.
/// </summary>
public sealed class ApiKeyAuthenticationOptions : AuthenticationSchemeOptions
{
    /// <summary>
    /// The expected API key value. Requests must send this in the X-Api-Key header.
    /// </summary>
    public string ApiKey { get; set; } = string.Empty;
}
```

**Step 2: Create the authentication handler**

```csharp
// src/agent/OpenAgent.Security.ApiKey/ApiKeyAuthenticationHandler.cs
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace OpenAgent.Security.ApiKey;

/// <summary>
/// Validates the X-Api-Key header against the configured API key.
/// Returns 401 if the key is missing or invalid.
/// </summary>
public sealed class ApiKeyAuthenticationHandler(
    IOptionsMonitor<ApiKeyAuthenticationOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder)
    : AuthenticationHandler<ApiKeyAuthenticationOptions>(options, logger, encoder)
{
    private const string HeaderName = "X-Api-Key";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        // Check for the API key header
        if (!Request.Headers.TryGetValue(HeaderName, out var headerValue))
            return Task.FromResult(AuthenticateResult.Fail("Missing X-Api-Key header."));

        var providedKey = headerValue.ToString();

        // Validate against configured key
        if (!string.Equals(providedKey, Options.ApiKey, StringComparison.Ordinal))
            return Task.FromResult(AuthenticateResult.Fail("Invalid API key."));

        // Build an authenticated identity
        var claims = new[] { new Claim(ClaimTypes.Name, "api-key-user") };
        var identity = new ClaimsIdentity(claims, Scheme.Name);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, Scheme.Name);

        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
```

**Step 3: Verify it builds**

```bash
cd src/agent && dotnet build
```

Expected: Build succeeded.

**Step 4: Commit**

```
feat: add API key authentication handler and options
```

---

### Task 3: Create the DI extension method

**Files:**
- Create: `src/agent/OpenAgent.Security.ApiKey/ApiKeyServiceExtensions.cs`

**Step 1: Create the extension method**

This is the single method that Program.cs calls. Later, `AddEntraIdAuth()` will have the same shape.

```csharp
// src/agent/OpenAgent.Security.ApiKey/ApiKeyServiceExtensions.cs
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace OpenAgent.Security.ApiKey;

/// <summary>
/// Registers API key authentication and authorization.
/// Swap this call for AddEntraIdAuth() when migrating to Entra ID.
/// </summary>
public static class ApiKeyServiceExtensions
{
    public const string SchemeName = "ApiKey";

    /// <summary>
    /// Adds API key authentication using the "Authentication:ApiKey" config value.
    /// </summary>
    public static IServiceCollection AddApiKeyAuth(this IServiceCollection services, IConfiguration configuration)
    {
        var apiKey = configuration["Authentication:ApiKey"]
            ?? throw new InvalidOperationException("Authentication:ApiKey is not configured.");

        services.AddAuthentication(SchemeName)
            .AddScheme<ApiKeyAuthenticationOptions, ApiKeyAuthenticationHandler>(SchemeName, options =>
            {
                options.ApiKey = apiKey;
            });

        services.AddAuthorization();

        return services;
    }
}
```

**Step 2: Verify it builds**

```bash
cd src/agent && dotnet build
```

Expected: Build succeeded.

**Step 3: Commit**

```
feat: add AddApiKeyAuth extension method for DI wiring
```

---

### Task 4: Wire auth into Program.cs and protect endpoints

**Files:**
- Modify: `src/agent/OpenAgent/Program.cs`

**Step 1: Add the using and call AddApiKeyAuth**

Add after the existing `using` statements:

```csharp
using OpenAgent.Security.ApiKey;
```

Add after the `builder.Services.AddSingleton<IConfigurable>(...)` block, before `var app = builder.Build();`:

```csharp
// Authentication — swap AddApiKeyAuth for AddEntraIdAuth when migrating to Entra ID
builder.Services.AddApiKeyAuth(builder.Configuration);
```

**Step 2: Add middleware after `app.UseWebSockets()`**

```csharp
app.UseAuthentication();
app.UseAuthorization();
```

**Step 3: Add `.RequireAuthorization()` to all endpoint groups**

The `/health` endpoint stays anonymous. All others require auth:

```csharp
app.MapGet("/health", () => Results.Ok()).AllowAnonymous();
app.MapConversationEndpoints();
app.MapChatEndpoints();
app.MapWebSocketVoiceEndpoints();
app.MapWebSocketTextEndpoints();
app.MapAdminEndpoints();
```

Each `Map*Endpoints` method needs `.RequireAuthorization()` on its routes. The cleanest way: add it to the `MapGroup` or individual `Map*` calls inside each endpoint file.

Update `ConversationEndpoints.cs` — add to the group:
```csharp
var group = app.MapGroup("/api/conversations").RequireAuthorization();
```

Update `AdminEndpoints.cs` — add to the group:
```csharp
var group = app.MapGroup("/api/admin/providers").RequireAuthorization();
```

Update `ChatEndpoints.cs` — add to the MapPost:
```csharp
app.MapPost("/api/conversations/{conversationId}/messages", ...).RequireAuthorization();
```

Update `WebSocketTextEndpoints.cs` — add to the Map:
```csharp
app.Map("/ws/conversations/{conversationId}/text", ...).RequireAuthorization();
```

Update `WebSocketVoiceEndpoints.cs` — add `.RequireAuthorization()` to its Map call.

**Step 4: Add config to appsettings.json (or appsettings.Development.json)**

Check if appsettings exists and add:

```json
{
  "Authentication": {
    "ApiKey": "dev-api-key-change-me"
  }
}
```

**Step 5: Verify it builds**

```bash
cd src/agent && dotnet build
```

Expected: Build succeeded.

**Step 6: Commit**

```
feat: wire API key auth into pipeline, protect all endpoints
```

---

### Task 5: Write integration tests for auth

**Files:**
- Create: `src/agent/OpenAgent.Tests/ApiKeyAuthTests.cs`
- Modify: `src/agent/OpenAgent.Tests/ChatEndpointTests.cs`

**Step 1: Write the auth test class**

```csharp
// src/agent/OpenAgent.Tests/ApiKeyAuthTests.cs
using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace OpenAgent.Tests;

/// <summary>
/// Verifies API key authentication rejects unauthenticated requests
/// and allows authenticated ones.
/// </summary>
public class ApiKeyAuthTests : IClassFixture<WebApplicationFactory<Program>>
{
    private const string ValidApiKey = "dev-api-key-change-me";
    private readonly WebApplicationFactory<Program> _factory;

    public ApiKeyAuthTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Request_WithoutApiKey_Returns401()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/conversations");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Request_WithInvalidApiKey_Returns401()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", "wrong-key");

        var response = await client.GetAsync("/api/conversations");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Request_WithValidApiKey_Returns200()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", ValidApiKey);

        var response = await client.GetAsync("/api/conversations");

        response.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task HealthEndpoint_WithoutApiKey_Returns200()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/health");

        response.EnsureSuccessStatusCode();
    }
}
```

**Step 2: Update existing ChatEndpointTests to include the API key**

The existing tests will start failing because they don't send the API key. Add the header to the test client:

```csharp
// In ChatEndpointTests constructor, after creating the factory:
// When creating the client, tests need to add the API key header.
// Add a helper or set DefaultRequestHeaders on each client.
```

The simplest fix: in each test where `_factory.CreateClient()` is called, add:
```csharp
client.DefaultRequestHeaders.Add("X-Api-Key", "dev-api-key-change-me");
```

**Step 3: Run the tests**

```bash
cd src/agent && dotnet test
```

Expected: All tests pass — existing tests still work with API key, new tests verify 401/200 behavior.

**Step 4: Commit**

```
test: add API key auth tests, update existing tests with API key header
```

---

### Task 6: Update CLAUDE.md

**Files:**
- Modify: `CLAUDE.md`

**Step 1: Update the project structure section**

Add to the project listing:
```
  OpenAgent.Security.ApiKey/              API key authentication — AddApiKeyAuth() extension
```

**Step 2: Add security section to Architecture Rules**

```markdown
### Authentication
Pluggable auth via extension methods on `IServiceCollection`. Currently `AddApiKeyAuth()` validates `X-Api-Key` header. Swap for `AddEntraIdAuth()` when migrating to Entra ID — same shape, different implementation. `/health` is anonymous, all other endpoints require authorization.
```

**Step 3: Commit**

```
docs: update CLAUDE.md with API key auth architecture
```
