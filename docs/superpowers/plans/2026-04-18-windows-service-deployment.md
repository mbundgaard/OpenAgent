# Windows Service Deployment Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Ship a single self-installing `OpenAgent.exe` that runs as a Windows service (`--install` / `--uninstall` / `--restart` / `--status`), with reach into user-configured paths via directory junctions.

**Architecture:** New `Installer/` folder inside `OpenAgent` for all Windows-specific install logic, behind an `ISystemCommandRunner` abstraction so unit tests assert on composed `sc.exe` / `netsh` / `mklink` commands without side effects. `Program.cs` gets a CLI dispatch at the very top that branches before host building. `DataDirectoryBootstrap` grows symlink-creation and a seeded `"symlinks": {}` default. Root resolution moves from an inline expression in `Program.cs` to a testable helper with `AppContext.BaseDirectory` as the new fallback.

**Tech Stack:** .NET 10, `Microsoft.Extensions.Hosting.WindowsServices`, `Microsoft.Extensions.Logging.EventLog`, xUnit.

**Spec:** [docs/superpowers/specs/2026-04-18-windows-service-deployment-design.md](../specs/2026-04-18-windows-service-deployment-design.md)

---

## File Map

### New files

| File | Responsibility |
|------|---------------|
| `src/agent/OpenAgent/RootResolver.cs` | Extracts `DATA_DIR`/fallback logic into a testable static helper |
| `src/agent/OpenAgent/Installer/ISystemCommandRunner.cs` | Abstraction over `Process.Start` for shelling out to `sc.exe`, `netsh`, `cmd /c mklink` |
| `src/agent/OpenAgent/Installer/SystemCommandRunner.cs` | Production implementation |
| `src/agent/OpenAgent/Installer/IDirectoryLinkCreator.cs` | Cross-platform directory link abstraction (junction on Windows, symlink on Linux) |
| `src/agent/OpenAgent/Installer/DirectoryLinkCreator.cs` | Implementation: Windows via `mklink /J`, Linux via `Directory.CreateSymbolicLink` |
| `src/agent/OpenAgent/Installer/ElevationCheck.cs` | `WindowsPrincipal.IsInRole(Administrator)` wrapper + `RequireAdmin(verb)` |
| `src/agent/OpenAgent/Installer/ServiceInstaller.cs` | `sc.exe` wrapper — create/failure/config/start/stop/delete/query, binPath quote escaping |
| `src/agent/OpenAgent/Installer/FirewallRule.cs` | `netsh advfirewall` wrapper — add/remove inbound TCP rule by name |
| `src/agent/OpenAgent/Installer/EventLogRegistrar.cs` | Ensures the `OpenAgent` event log source exists (creates on first `--install`) |
| `src/agent/OpenAgent/Installer/PreInstallChecks.cs` | Verifies `node\baileys-bridge.js`, Node.js on `PATH`, path sanity |
| `src/agent/OpenAgent/Installer/InstallerCli.cs` | Parses install-mode args, orchestrates the pre-check → copy → register → start flow |
| `src/agent/OpenAgent.Tests/RootResolverTests.cs` | `DATA_DIR` set wins; unset falls back to `AppContext.BaseDirectory` |
| `src/agent/OpenAgent.Tests/DataDirectoryBootstrapTests.cs` | Seed, idempotent symlinks, wrong-target warning, real-dir-at-path warning |
| `src/agent/OpenAgent.Tests/Installer/FakeSystemCommandRunner.cs` | Test double that records `(executable, args)` tuples + returns canned exit codes/stdout |
| `src/agent/OpenAgent.Tests/Installer/ServiceInstallerTests.cs` | Asserts composed `sc.exe` arg strings match spec, including binPath quote escaping |
| `src/agent/OpenAgent.Tests/Installer/FirewallRuleTests.cs` | Asserts composed `netsh` arg strings |
| `src/agent/OpenAgent.Tests/Installer/DirectoryLinkCreatorTests.cs` | Linux branch uses real filesystem; Windows branch asserts `mklink /J` composition |
| `src/agent/OpenAgent.Tests/Installer/PreInstallChecksTests.cs` | Temp-dir with/without bridge.js, mocked `node --version` success/failure, path-sanity rejection |

### Modified files

| File | Change |
|------|--------|
| `src/agent/Directory.Packages.props` | Add `PackageVersion` entries for `Microsoft.Extensions.Hosting.WindowsServices` (10.0.3) and `Microsoft.Extensions.Logging.EventLog` (10.0.3) |
| `src/agent/OpenAgent/OpenAgent.csproj` | Add `<RuntimeIdentifiers>linux-x64;win-x64</RuntimeIdentifiers>`; `<PackageReference>` entries for the two new packages |
| `src/agent/OpenAgent/Program.cs` | (1) Line 32 — replace inline root resolution with `RootResolver.Resolve()`. (2) At the top of the file, dispatch `--install` / `--uninstall` / `--restart` / `--status` via `InstallerCli` and return before `WebApplication.CreateBuilder`. (3) `AddWindowsService()` + `AddEventLog()` when `--service` is present. |
| `src/agent/OpenAgent/DataDirectoryBootstrap.cs` | (1) Change default `agent.json` content from `"{}"` to `"{\"symlinks\": {}}"`. (2) After defaults extraction, read `symlinks` block from `config/agent.json` and call `IDirectoryLinkCreator` for each entry. |
| `src/agent/OpenAgent.Tests/OpenAgent.Tests.csproj` | No new ProjectReference needed (tests already reference `OpenAgent`). |

### NOT touched

- `Dockerfile` — existing `DATA_DIR=/home/data` behavior preserved by default.
- `appsettings.json` / `appsettings.Development.json` — port 8080 default unchanged.
- Any provider / channel / tool project.
- `OpenAgent.Tests/TestSetup.cs` — its `/home/data` fallback is test-only and benign.

---

## Task 1: Package and runtime identifier setup

**Files:**
- Modify: `src/agent/Directory.Packages.props`
- Modify: `src/agent/OpenAgent/OpenAgent.csproj`

- [ ] **Step 1: Add package versions to central management**

Modify `src/agent/Directory.Packages.props`. Add two `<PackageVersion>` entries inside the existing `<ItemGroup>`:

```xml
    <PackageVersion Include="Microsoft.Extensions.Hosting.WindowsServices" Version="10.0.3" />
    <PackageVersion Include="Microsoft.Extensions.Logging.EventLog" Version="10.0.3" />
```

- [ ] **Step 2: Reference the packages and add win-x64 RID**

Modify `src/agent/OpenAgent/OpenAgent.csproj`. Add a `<PropertyGroup>` after the first `<ItemGroup>` (the one with `InternalsVisibleTo`):

```xml
  <PropertyGroup>
    <RuntimeIdentifiers>linux-x64;win-x64</RuntimeIdentifiers>
  </PropertyGroup>
```

In the existing `<ItemGroup>` that holds Serilog packages, add:

```xml
    <PackageReference Include="Microsoft.Extensions.Hosting.WindowsServices" />
    <PackageReference Include="Microsoft.Extensions.Logging.EventLog" />
```

- [ ] **Step 3: Build to verify**

```bash
cd src/agent && dotnet build
```

Expected: Build succeeds with no errors.

- [ ] **Step 4: Commit**

```bash
git add src/agent/Directory.Packages.props src/agent/OpenAgent/OpenAgent.csproj
git commit -m "chore: add windows-service packages and win-x64 RID"
```

---

## Task 2: RootResolver — extract root resolution into testable helper

**Files:**
- Create: `src/agent/OpenAgent/RootResolver.cs`
- Create: `src/agent/OpenAgent.Tests/RootResolverTests.cs`
- Modify: `src/agent/OpenAgent/Program.cs`

- [ ] **Step 1: Write the failing tests**

Create `src/agent/OpenAgent.Tests/RootResolverTests.cs`:

```csharp
using OpenAgent;

namespace OpenAgent.Tests;

public class RootResolverTests
{
    [Fact]
    public void Resolve_WithDataDirSet_ReturnsEnvVarValue()
    {
        var result = RootResolver.Resolve(envVar: "/tmp/from-env", baseDirectory: "C:/app");

        Assert.Equal("/tmp/from-env", result);
    }

    [Fact]
    public void Resolve_WithDataDirUnset_ReturnsBaseDirectory()
    {
        var result = RootResolver.Resolve(envVar: null, baseDirectory: "C:/app");

        Assert.Equal("C:/app", result);
    }

    [Fact]
    public void Resolve_WithEmptyEnvVar_FallsBackToBaseDirectory()
    {
        var result = RootResolver.Resolve(envVar: "", baseDirectory: "C:/app");

        Assert.Equal("C:/app", result);
    }

    [Fact]
    public void Resolve_WithWhitespaceEnvVar_FallsBackToBaseDirectory()
    {
        var result = RootResolver.Resolve(envVar: "   ", baseDirectory: "C:/app");

        Assert.Equal("C:/app", result);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

```bash
cd src/agent && dotnet test --filter "FullyQualifiedName~RootResolverTests"
```

Expected: FAIL with "The type or namespace name 'RootResolver' could not be found."

- [ ] **Step 3: Create RootResolver**

Create `src/agent/OpenAgent/RootResolver.cs`:

```csharp
namespace OpenAgent;

/// <summary>
/// Resolves the agent's root directory. Reads the DATA_DIR environment variable first;
/// if unset, empty, or whitespace-only, falls back to the directory containing the running executable.
/// The env-var override behavior matches the existing Linux/Docker deployment; the fallback
/// is new and lets the Windows service run without any environment configuration.
/// </summary>
public static class RootResolver
{
    /// <summary>Resolves the root directory using the real environment.</summary>
    public static string Resolve() =>
        Resolve(
            envVar: Environment.GetEnvironmentVariable("DATA_DIR"),
            baseDirectory: AppContext.BaseDirectory);

    /// <summary>Test-friendly overload that takes both inputs explicitly.</summary>
    public static string Resolve(string? envVar, string baseDirectory) =>
        string.IsNullOrWhiteSpace(envVar) ? baseDirectory : envVar;
}
```

- [ ] **Step 4: Run test to verify it passes**

```bash
cd src/agent && dotnet test --filter "FullyQualifiedName~RootResolverTests"
```

Expected: PASS 4 tests.

- [ ] **Step 5: Wire RootResolver into Program.cs**

Modify `src/agent/OpenAgent/Program.cs`. Replace lines 30-33:

```csharp
var environment = new AgentEnvironment
{
    DataPath = Environment.GetEnvironmentVariable("DATA_DIR") ?? "/home/data"
};
```

with:

```csharp
var environment = new AgentEnvironment
{
    DataPath = RootResolver.Resolve()
};
```

- [ ] **Step 6: Build + existing tests**

```bash
cd src/agent && dotnet build && dotnet test
```

Expected: build passes, all existing tests still pass.

- [ ] **Step 7: Commit**

```bash
git add src/agent/OpenAgent/RootResolver.cs \
        src/agent/OpenAgent/Program.cs \
        src/agent/OpenAgent.Tests/RootResolverTests.cs
git commit -m "feat: extract RootResolver with AppContext.BaseDirectory fallback"
```

---

## Task 3: DataDirectoryBootstrap — seed `"symlinks": {}` in default agent.json

**Files:**
- Modify: `src/agent/OpenAgent/DataDirectoryBootstrap.cs`
- Create: `src/agent/OpenAgent.Tests/DataDirectoryBootstrapTests.cs`

- [ ] **Step 1: Write the failing tests**

Create `src/agent/OpenAgent.Tests/DataDirectoryBootstrapTests.cs`:

```csharp
using OpenAgent;

namespace OpenAgent.Tests;

public class DataDirectoryBootstrapTests : IDisposable
{
    private readonly string _tempDir;

    public DataDirectoryBootstrapTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "openagent-bootstrap-" + Guid.NewGuid());
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    [Fact]
    public void Run_OnFreshDirectory_SeedsAgentJsonWithEmptySymlinksBlock()
    {
        DataDirectoryBootstrap.Run(_tempDir);

        var agentJson = File.ReadAllText(Path.Combine(_tempDir, "config", "agent.json"));
        Assert.Equal("{\"symlinks\": {}}", agentJson);
    }

    [Fact]
    public void Run_WithExistingAgentJson_PreservesContent()
    {
        var configDir = Path.Combine(_tempDir, "config");
        Directory.CreateDirectory(configDir);
        var existing = "{\"textProvider\":\"custom\"}";
        File.WriteAllText(Path.Combine(configDir, "agent.json"), existing);

        DataDirectoryBootstrap.Run(_tempDir);

        Assert.Equal(existing, File.ReadAllText(Path.Combine(configDir, "agent.json")));
    }
}
```

- [ ] **Step 2: Run test to verify first test fails**

```bash
cd src/agent && dotnet test --filter "FullyQualifiedName~DataDirectoryBootstrapTests.Run_OnFreshDirectory_SeedsAgentJsonWithEmptySymlinksBlock"
```

Expected: FAIL — current default is `{}`, not `{"symlinks": {}}`.

- [ ] **Step 3: Update the bootstrap default**

Modify `src/agent/OpenAgent/DataDirectoryBootstrap.cs`. Replace line 63:

```csharp
            File.WriteAllText(agentConfigPath, "{}");
```

with:

```csharp
            File.WriteAllText(agentConfigPath, "{\"symlinks\": {}}");
```

- [ ] **Step 4: Run tests to verify both pass**

```bash
cd src/agent && dotnet test --filter "FullyQualifiedName~DataDirectoryBootstrapTests"
```

Expected: PASS 2 tests.

- [ ] **Step 5: Commit**

```bash
git add src/agent/OpenAgent/DataDirectoryBootstrap.cs \
        src/agent/OpenAgent.Tests/DataDirectoryBootstrapTests.cs
git commit -m "feat(bootstrap): seed empty symlinks block in default agent.json"
```

---

## Task 4: ISystemCommandRunner — shell-out abstraction + fake for tests

**Files:**
- Create: `src/agent/OpenAgent/Installer/ISystemCommandRunner.cs`
- Create: `src/agent/OpenAgent/Installer/SystemCommandRunner.cs`
- Create: `src/agent/OpenAgent.Tests/Installer/FakeSystemCommandRunner.cs`

- [ ] **Step 1: Define the interface**

Create `src/agent/OpenAgent/Installer/ISystemCommandRunner.cs`:

```csharp
namespace OpenAgent.Installer;

/// <summary>
/// Abstraction over Process.Start for install-mode shell-outs (sc.exe, netsh, cmd /c mklink).
/// Tests substitute a FakeSystemCommandRunner that records calls and returns canned results.
/// </summary>
public interface ISystemCommandRunner
{
    /// <summary>
    /// Runs the specified executable with arguments and returns the exit code and merged stdout/stderr.
    /// Blocks until the process exits or the timeout elapses.
    /// </summary>
    CommandResult Run(string executable, string arguments, TimeSpan? timeout = null);
}

public sealed record CommandResult(int ExitCode, string Output);
```

- [ ] **Step 2: Implement the production runner**

Create `src/agent/OpenAgent/Installer/SystemCommandRunner.cs`:

```csharp
using System.Diagnostics;

namespace OpenAgent.Installer;

public sealed class SystemCommandRunner : ISystemCommandRunner
{
    public CommandResult Run(string executable, string arguments, TimeSpan? timeout = null)
    {
        var psi = new ProcessStartInfo
        {
            FileName = executable,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(psi) ?? throw new InvalidOperationException($"Failed to start {executable}");

        var effectiveTimeout = timeout ?? TimeSpan.FromSeconds(30);
        if (!process.WaitForExit((int)effectiveTimeout.TotalMilliseconds))
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException($"{executable} did not exit within {effectiveTimeout.TotalSeconds}s");
        }

        var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        return new CommandResult(process.ExitCode, output);
    }
}
```

- [ ] **Step 3: Implement the fake**

Create `src/agent/OpenAgent.Tests/Installer/FakeSystemCommandRunner.cs`:

```csharp
using OpenAgent.Installer;

namespace OpenAgent.Tests.Installer;

public sealed class FakeSystemCommandRunner : ISystemCommandRunner
{
    public List<(string Executable, string Arguments)> Calls { get; } = new();
    public Queue<CommandResult> Responses { get; } = new();

    public CommandResult Run(string executable, string arguments, TimeSpan? timeout = null)
    {
        Calls.Add((executable, arguments));
        return Responses.Count > 0
            ? Responses.Dequeue()
            : new CommandResult(0, "");
    }
}
```

- [ ] **Step 4: Build to verify**

```bash
cd src/agent && dotnet build
```

Expected: Build succeeds.

- [ ] **Step 5: Commit**

```bash
git add src/agent/OpenAgent/Installer/ISystemCommandRunner.cs \
        src/agent/OpenAgent/Installer/SystemCommandRunner.cs \
        src/agent/OpenAgent.Tests/Installer/FakeSystemCommandRunner.cs
git commit -m "feat(installer): add ISystemCommandRunner abstraction"
```

---

## Task 5: DirectoryLinkCreator — junction (Windows) / symlink (Linux)

**Files:**
- Create: `src/agent/OpenAgent/Installer/IDirectoryLinkCreator.cs`
- Create: `src/agent/OpenAgent/Installer/DirectoryLinkCreator.cs`
- Create: `src/agent/OpenAgent.Tests/Installer/DirectoryLinkCreatorTests.cs`

- [ ] **Step 1: Write the failing tests**

Create `src/agent/OpenAgent.Tests/Installer/DirectoryLinkCreatorTests.cs`:

```csharp
using OpenAgent.Installer;

namespace OpenAgent.Tests.Installer;

public class DirectoryLinkCreatorTests : IDisposable
{
    private readonly string _tempDir;

    public DirectoryLinkCreatorTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "openagent-link-" + Guid.NewGuid());
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    [Fact]
    public void CreateLink_WithWindowsOs_ComposesMklinkJunctionCommand()
    {
        var runner = new FakeSystemCommandRunner();
        var creator = new DirectoryLinkCreator(runner, isWindows: true);
        var linkPath = Path.Combine(_tempDir, "media");
        var targetPath = @"D:\Media";

        creator.CreateLink(linkPath, targetPath);

        Assert.Single(runner.Calls);
        var (exe, args) = runner.Calls[0];
        Assert.Equal("cmd.exe", exe);
        Assert.Equal($"/c mklink /J \"{linkPath}\" \"{targetPath}\"", args);
    }

    [Fact]
    public void CreateLink_WithLinuxOs_CreatesRealSymlink()
    {
        var target = Path.Combine(_tempDir, "target");
        Directory.CreateDirectory(target);
        var linkPath = Path.Combine(_tempDir, "link");
        var runner = new FakeSystemCommandRunner();
        var creator = new DirectoryLinkCreator(runner, isWindows: false);

        creator.CreateLink(linkPath, target);

        Assert.Empty(runner.Calls);
        Assert.True(new DirectoryInfo(linkPath).LinkTarget == target
                    || new DirectoryInfo(linkPath).ResolveLinkTarget(true)?.FullName == new DirectoryInfo(target).FullName);
    }

    [Fact]
    public void LinkExists_OnExistingJunction_ReturnsTrue()
    {
        var target = Path.Combine(_tempDir, "t");
        Directory.CreateDirectory(target);
        var linkPath = Path.Combine(_tempDir, "l");
        var runner = new FakeSystemCommandRunner();
        var creator = new DirectoryLinkCreator(runner, isWindows: false);
        creator.CreateLink(linkPath, target);

        Assert.True(creator.LinkExists(linkPath));
    }

    [Fact]
    public void LinkExists_OnMissingPath_ReturnsFalse()
    {
        var runner = new FakeSystemCommandRunner();
        var creator = new DirectoryLinkCreator(runner, isWindows: false);

        Assert.False(creator.LinkExists(Path.Combine(_tempDir, "nope")));
    }

    [Fact]
    public void ReadLinkTarget_OnSymlink_ReturnsTargetPath()
    {
        var target = Path.Combine(_tempDir, "t");
        Directory.CreateDirectory(target);
        var linkPath = Path.Combine(_tempDir, "l");
        var runner = new FakeSystemCommandRunner();
        var creator = new DirectoryLinkCreator(runner, isWindows: false);
        creator.CreateLink(linkPath, target);

        var resolved = creator.ReadLinkTarget(linkPath);

        Assert.Equal(target, resolved);
    }
}
```

- [ ] **Step 2: Define the interface**

Create `src/agent/OpenAgent/Installer/IDirectoryLinkCreator.cs`:

```csharp
namespace OpenAgent.Installer;

/// <summary>
/// Creates directory links that behave like transparent path mounts:
/// directory junctions on Windows (no admin needed), symlinks on Linux.
/// </summary>
public interface IDirectoryLinkCreator
{
    /// <summary>Creates a directory link at <paramref name="linkPath"/> pointing to <paramref name="targetPath"/>.</summary>
    void CreateLink(string linkPath, string targetPath);

    /// <summary>Returns true if a link (junction or symlink) exists at the path.</summary>
    bool LinkExists(string linkPath);

    /// <summary>Returns the resolved target of an existing link, or null if the path is not a link.</summary>
    string? ReadLinkTarget(string linkPath);
}
```

- [ ] **Step 3: Implement the creator**

Create `src/agent/OpenAgent/Installer/DirectoryLinkCreator.cs`:

```csharp
using System.Runtime.InteropServices;

namespace OpenAgent.Installer;

public sealed class DirectoryLinkCreator : IDirectoryLinkCreator
{
    private readonly ISystemCommandRunner _runner;
    private readonly bool _isWindows;

    public DirectoryLinkCreator(ISystemCommandRunner runner)
        : this(runner, RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) { }

    internal DirectoryLinkCreator(ISystemCommandRunner runner, bool isWindows)
    {
        _runner = runner;
        _isWindows = isWindows;
    }

    public void CreateLink(string linkPath, string targetPath)
    {
        if (_isWindows)
        {
            var result = _runner.Run("cmd.exe", $"/c mklink /J \"{linkPath}\" \"{targetPath}\"");
            if (result.ExitCode != 0)
                throw new IOException($"mklink /J failed (exit {result.ExitCode}): {result.Output}");
        }
        else
        {
            Directory.CreateSymbolicLink(linkPath, targetPath);
        }
    }

    public bool LinkExists(string linkPath)
    {
        var info = new DirectoryInfo(linkPath);
        return info.Exists && (info.Attributes & FileAttributes.ReparsePoint) != 0;
    }

    public string? ReadLinkTarget(string linkPath)
    {
        var info = new DirectoryInfo(linkPath);
        if (!info.Exists || (info.Attributes & FileAttributes.ReparsePoint) == 0)
            return null;

        return info.LinkTarget
               ?? info.ResolveLinkTarget(returnFinalTarget: true)?.FullName;
    }
}
```

- [ ] **Step 4: Run tests to verify**

```bash
cd src/agent && dotnet test --filter "FullyQualifiedName~DirectoryLinkCreatorTests"
```

Expected: PASS 5 tests on Linux. On Windows, the first test (mklink composition) passes; the others pass on both platforms because they use `isWindows: false`.

- [ ] **Step 5: Commit**

```bash
git add src/agent/OpenAgent/Installer/IDirectoryLinkCreator.cs \
        src/agent/OpenAgent/Installer/DirectoryLinkCreator.cs \
        src/agent/OpenAgent.Tests/Installer/DirectoryLinkCreatorTests.cs
git commit -m "feat(installer): add IDirectoryLinkCreator with junction/symlink branches"
```

---

## Task 6: DataDirectoryBootstrap — wire symlink block into Run()

**Files:**
- Modify: `src/agent/OpenAgent/DataDirectoryBootstrap.cs`
- Modify: `src/agent/OpenAgent.Tests/DataDirectoryBootstrapTests.cs`

- [ ] **Step 1: Add the failing tests**

Append these tests to `src/agent/OpenAgent.Tests/DataDirectoryBootstrapTests.cs`, inside the class before the closing `}`:

```csharp
    [Fact]
    public void Run_WithConfiguredSymlink_CreatesLinkToTarget()
    {
        var target = Path.Combine(_tempDir, "media-target");
        Directory.CreateDirectory(target);
        var configDir = Path.Combine(_tempDir, "config");
        Directory.CreateDirectory(configDir);
        File.WriteAllText(
            Path.Combine(configDir, "agent.json"),
            $"{{\"symlinks\":{{\"media\":\"{target.Replace("\\", "\\\\")}\"}}}}");

        DataDirectoryBootstrap.Run(_tempDir);

        var linkPath = Path.Combine(_tempDir, "media");
        Assert.True(new DirectoryInfo(linkPath).Exists);
        Assert.True((new DirectoryInfo(linkPath).Attributes & FileAttributes.ReparsePoint) != 0);
    }

    [Fact]
    public void Run_WithExistingCorrectSymlink_IsIdempotent()
    {
        var target = Path.Combine(_tempDir, "media-target");
        Directory.CreateDirectory(target);
        var configDir = Path.Combine(_tempDir, "config");
        Directory.CreateDirectory(configDir);
        File.WriteAllText(
            Path.Combine(configDir, "agent.json"),
            $"{{\"symlinks\":{{\"media\":\"{target.Replace("\\", "\\\\")}\"}}}}");

        DataDirectoryBootstrap.Run(_tempDir);
        DataDirectoryBootstrap.Run(_tempDir);

        var linkPath = Path.Combine(_tempDir, "media");
        Assert.True(new DirectoryInfo(linkPath).Exists);
    }

    [Fact]
    public void Run_WithRegularDirectoryAtLinkPath_LeavesItUntouched()
    {
        var target = Path.Combine(_tempDir, "media-target");
        Directory.CreateDirectory(target);
        var existingDir = Path.Combine(_tempDir, "media");
        Directory.CreateDirectory(existingDir);
        var marker = Path.Combine(existingDir, "marker.txt");
        File.WriteAllText(marker, "do not delete");

        var configDir = Path.Combine(_tempDir, "config");
        Directory.CreateDirectory(configDir);
        File.WriteAllText(
            Path.Combine(configDir, "agent.json"),
            $"{{\"symlinks\":{{\"media\":\"{target.Replace("\\", "\\\\")}\"}}}}");

        DataDirectoryBootstrap.Run(_tempDir);

        Assert.True(File.Exists(marker));
        Assert.Equal("do not delete", File.ReadAllText(marker));
    }
```

- [ ] **Step 2: Run tests to verify they fail**

```bash
cd src/agent && dotnet test --filter "FullyQualifiedName~DataDirectoryBootstrapTests"
```

Expected: FAIL on the three new tests — bootstrap doesn't read `symlinks` yet.

- [ ] **Step 3: Update DataDirectoryBootstrap**

Modify `src/agent/OpenAgent/DataDirectoryBootstrap.cs`. Replace the entire file with:

```csharp
using System.Reflection;
using System.Text.Json;
using OpenAgent.Installer;

namespace OpenAgent;

/// <summary>
/// Ensures the data directory has the required folder structure and default files.
/// Runs once at startup — creates missing folders, extracts embedded default files,
/// and creates configured directory links (junctions on Windows, symlinks on Linux).
/// Never overwrites existing files; reacts to invalid symlink configuration with warnings,
/// not failures.
/// </summary>
public static class DataDirectoryBootstrap
{
    private static readonly string[] RequiredDirectories =
    [
        "projects",
        "repos",
        "memory",
        "config",
        "connections",
        "skills"
    ];

    /// <summary>Production entry point. Uses <see cref="SystemCommandRunner"/> for link creation on Windows.</summary>
    public static void Run(string dataPath) => Run(dataPath, new DirectoryLinkCreator(new SystemCommandRunner()));

    /// <summary>Test-friendly overload that accepts an injected link creator.</summary>
    public static void Run(string dataPath, IDirectoryLinkCreator linkCreator)
    {
        // Ensure required directories exist
        foreach (var dir in RequiredDirectories)
            Directory.CreateDirectory(Path.Combine(dataPath, dir));

        // Extract embedded default markdown files
        var assembly = Assembly.GetExecutingAssembly();
        var prefix = "OpenAgent.defaults.";

        // If any personality file exists, this isn't a first run — don't re-create BOOTSTRAP.md
        var isFirstRun = !File.Exists(Path.Combine(dataPath, "AGENTS.md"));

        foreach (var resourceName in assembly.GetManifestResourceNames())
        {
            if (!resourceName.StartsWith(prefix))
                continue;

            var fileName = resourceName[prefix.Length..];
            var targetPath = Path.Combine(dataPath, fileName);

            if (File.Exists(targetPath))
                continue;

            if (fileName == "BOOTSTRAP.md" && !isFirstRun)
                continue;

            using var stream = assembly.GetManifestResourceStream(resourceName)!;
            using var reader = new StreamReader(stream);
            File.WriteAllText(targetPath, reader.ReadToEnd());
        }

        // Write default config files if missing
        var agentConfigPath = Path.Combine(dataPath, "config", "agent.json");
        if (!File.Exists(agentConfigPath))
            File.WriteAllText(agentConfigPath, "{\"symlinks\": {}}");

        var connectionsPath = Path.Combine(dataPath, "config", "connections.json");
        if (!File.Exists(connectionsPath))
            File.WriteAllText(connectionsPath, "[]");

        // Create configured directory links
        CreateConfiguredLinks(dataPath, agentConfigPath, linkCreator);
    }

    private static void CreateConfiguredLinks(string dataPath, string agentConfigPath, IDirectoryLinkCreator linkCreator)
    {
        Dictionary<string, string>? symlinks = null;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(agentConfigPath));
            if (doc.RootElement.TryGetProperty("symlinks", out var symlinksElement)
                && symlinksElement.ValueKind == JsonValueKind.Object)
            {
                symlinks = new Dictionary<string, string>();
                foreach (var prop in symlinksElement.EnumerateObject())
                {
                    if (prop.Value.ValueKind == JsonValueKind.String)
                        symlinks[prop.Name] = prop.Value.GetString()!;
                }
            }
        }
        catch (JsonException)
        {
            Console.Error.WriteLine($"[bootstrap] Failed to parse {agentConfigPath}; skipping symlink creation.");
            return;
        }

        if (symlinks is null)
            return;

        foreach (var (name, target) in symlinks)
        {
            var linkPath = Path.Combine(dataPath, name);

            if (linkCreator.LinkExists(linkPath))
            {
                var existingTarget = linkCreator.ReadLinkTarget(linkPath);
                if (existingTarget is not null && PathsEqual(existingTarget, target))
                    continue;

                Console.Error.WriteLine(
                    $"[bootstrap] Link {linkPath} already exists and points to {existingTarget ?? "<unknown>"}; " +
                    $"configured target {target} ignored.");
                continue;
            }

            if (Directory.Exists(linkPath) || File.Exists(linkPath))
            {
                Console.Error.WriteLine(
                    $"[bootstrap] {linkPath} exists as a regular directory/file; skipping junction creation.");
                continue;
            }

            if (!Directory.Exists(target))
            {
                Console.Error.WriteLine(
                    $"[bootstrap] Symlink target {target} does not exist; skipping junction {linkPath}.");
                continue;
            }

            try
            {
                linkCreator.CreateLink(linkPath, target);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[bootstrap] Failed to create junction {linkPath} -> {target}: {ex.Message}");
            }
        }
    }

    private static bool PathsEqual(string a, string b) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
```

- [ ] **Step 4: Run all bootstrap tests**

```bash
cd src/agent && dotnet test --filter "FullyQualifiedName~DataDirectoryBootstrapTests"
```

Expected: PASS all 5 tests (2 from Task 3 + 3 new).

- [ ] **Step 5: Run full test suite**

```bash
cd src/agent && dotnet test
```

Expected: All existing tests still pass.

- [ ] **Step 6: Commit**

```bash
git add src/agent/OpenAgent/DataDirectoryBootstrap.cs \
        src/agent/OpenAgent.Tests/DataDirectoryBootstrapTests.cs
git commit -m "feat(bootstrap): create configured directory junctions from agent.json"
```

---

## Task 7: ElevationCheck — WindowsPrincipal wrapper

**Files:**
- Create: `src/agent/OpenAgent/Installer/ElevationCheck.cs`

- [ ] **Step 1: Create the helper**

Create `src/agent/OpenAgent/Installer/ElevationCheck.cs`:

```csharp
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace OpenAgent.Installer;

/// <summary>
/// Elevation/admin checks for install-mode commands. On non-Windows platforms these are
/// no-ops (IsAdministrator returns true) — install-mode commands are Windows-only and
/// their main entry points already gate on OS.
/// </summary>
public static class ElevationCheck
{
    /// <summary>True if the current process runs as a member of the local Administrators group.</summary>
    public static bool IsAdministrator()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return true;

#pragma warning disable CA1416 // validated by IsOSPlatform above
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
#pragma warning restore CA1416
    }

    /// <summary>
    /// Returns 0 if elevated. Otherwise writes an explanation to stderr and returns a non-zero exit code
    /// for the caller to pass back from Main.
    /// </summary>
    public static int RequireAdministrator(string verb)
    {
        if (IsAdministrator())
            return 0;

        Console.Error.WriteLine($"{verb} requires administrator privileges. Run this command from an elevated (\"Run as Administrator\") prompt.");
        return 5; // ERROR_ACCESS_DENIED
    }
}
```

- [ ] **Step 2: Build**

```bash
cd src/agent && dotnet build
```

Expected: Build succeeds.

- [ ] **Step 3: Commit**

Unit tests for `IsAdministrator` aren't meaningful (the result depends on how the test process was launched). Document that this is covered by manual QA in the end-to-end task.

```bash
git add src/agent/OpenAgent/Installer/ElevationCheck.cs
git commit -m "feat(installer): add ElevationCheck with WindowsPrincipal.IsInRole"
```

---

## Task 8: ServiceInstaller — sc.exe wrapper

**Files:**
- Create: `src/agent/OpenAgent/Installer/ServiceInstaller.cs`
- Create: `src/agent/OpenAgent.Tests/Installer/ServiceInstallerTests.cs`

- [ ] **Step 1: Write the failing tests**

Create `src/agent/OpenAgent.Tests/Installer/ServiceInstallerTests.cs`:

```csharp
using OpenAgent.Installer;

namespace OpenAgent.Tests.Installer;

public class ServiceInstallerTests
{
    [Fact]
    public void Create_ComposesScCreateWithQuoteEscapedBinPath()
    {
        var runner = new FakeSystemCommandRunner();
        var installer = new ServiceInstaller(runner);

        installer.Create(serviceName: "OpenAgent",
            exePath: @"C:\Program Files\OpenAgent\OpenAgent.exe",
            displayName: "OpenAgent",
            description: "Multi-channel AI agent platform");

        Assert.Equal(3, runner.Calls.Count);
        Assert.Equal(("sc.exe",
            "create OpenAgent binPath= \"\\\"C:\\Program Files\\OpenAgent\\OpenAgent.exe\\\" --service\" start= auto DisplayName= \"OpenAgent\""),
            runner.Calls[0]);
        Assert.Equal(("sc.exe", "description OpenAgent \"Multi-channel AI agent platform\""),
            runner.Calls[1]);
        Assert.Equal(("sc.exe", "failure OpenAgent reset= 86400 actions= restart/5000/restart/5000/restart/60000"),
            runner.Calls[2]);
    }

    [Fact]
    public void Start_CallsScStart()
    {
        var runner = new FakeSystemCommandRunner();
        var installer = new ServiceInstaller(runner);

        installer.Start("OpenAgent");

        Assert.Single(runner.Calls);
        Assert.Equal(("sc.exe", "start OpenAgent"), runner.Calls[0]);
    }

    [Fact]
    public void Stop_CallsScStop()
    {
        var runner = new FakeSystemCommandRunner();
        var installer = new ServiceInstaller(runner);

        installer.Stop("OpenAgent");

        Assert.Single(runner.Calls);
        Assert.Equal(("sc.exe", "stop OpenAgent"), runner.Calls[0]);
    }

    [Fact]
    public void Delete_CallsScDelete()
    {
        var runner = new FakeSystemCommandRunner();
        var installer = new ServiceInstaller(runner);

        installer.Delete("OpenAgent");

        Assert.Single(runner.Calls);
        Assert.Equal(("sc.exe", "delete OpenAgent"), runner.Calls[0]);
    }

    [Fact]
    public void UpdateBinPath_CallsScConfigWithQuoteEscapedPath()
    {
        var runner = new FakeSystemCommandRunner();
        var installer = new ServiceInstaller(runner);

        installer.UpdateBinPath("OpenAgent", @"C:\OpenAgent\OpenAgent.exe");

        Assert.Single(runner.Calls);
        Assert.Equal(("sc.exe",
            "config OpenAgent binPath= \"\\\"C:\\OpenAgent\\OpenAgent.exe\\\" --service\""),
            runner.Calls[0]);
    }

    [Fact]
    public void IsInstalled_ReturnsTrueOnScQueryExit0()
    {
        var runner = new FakeSystemCommandRunner();
        runner.Responses.Enqueue(new CommandResult(0, "SERVICE_NAME: OpenAgent"));
        var installer = new ServiceInstaller(runner);

        Assert.True(installer.IsInstalled("OpenAgent"));
        Assert.Equal(("sc.exe", "query OpenAgent"), runner.Calls[0]);
    }

    [Fact]
    public void IsInstalled_ReturnsFalseOnScQueryNonZero()
    {
        var runner = new FakeSystemCommandRunner();
        runner.Responses.Enqueue(new CommandResult(1060, "The specified service does not exist"));
        var installer = new ServiceInstaller(runner);

        Assert.False(installer.IsInstalled("OpenAgent"));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

```bash
cd src/agent && dotnet test --filter "FullyQualifiedName~ServiceInstallerTests"
```

Expected: FAIL — `ServiceInstaller` doesn't exist.

- [ ] **Step 3: Implement ServiceInstaller**

Create `src/agent/OpenAgent/Installer/ServiceInstaller.cs`:

```csharp
namespace OpenAgent.Installer;

/// <summary>
/// Wraps sc.exe for creating, configuring, starting, stopping, querying, and deleting
/// the Windows service. The binPath argument requires the exe path to be wrapped in
/// escaped quotes so paths containing spaces (e.g. C:\Program Files\OpenAgent\...)
/// survive the sc parser.
/// </summary>
public sealed class ServiceInstaller
{
    private readonly ISystemCommandRunner _runner;

    public ServiceInstaller(ISystemCommandRunner runner) => _runner = runner;

    public void Create(string serviceName, string exePath, string displayName, string description)
    {
        var binPath = $"\\\"{exePath}\\\" --service";
        Run($"create {serviceName} binPath= \"{binPath}\" start= auto DisplayName= \"{displayName}\"");
        Run($"description {serviceName} \"{description}\"");
        Run($"failure {serviceName} reset= 86400 actions= restart/5000/restart/5000/restart/60000");
    }

    public void Start(string serviceName) => Run($"start {serviceName}");
    public void Stop(string serviceName) => Run($"stop {serviceName}");
    public void Delete(string serviceName) => Run($"delete {serviceName}");

    public void UpdateBinPath(string serviceName, string exePath)
    {
        var binPath = $"\\\"{exePath}\\\" --service";
        Run($"config {serviceName} binPath= \"{binPath}\"");
    }

    public bool IsInstalled(string serviceName)
    {
        var result = _runner.Run("sc.exe", $"query {serviceName}");
        return result.ExitCode == 0;
    }

    private CommandResult Run(string args) => _runner.Run("sc.exe", args);
}
```

- [ ] **Step 4: Run tests to verify they pass**

```bash
cd src/agent && dotnet test --filter "FullyQualifiedName~ServiceInstallerTests"
```

Expected: PASS 7 tests.

- [ ] **Step 5: Commit**

```bash
git add src/agent/OpenAgent/Installer/ServiceInstaller.cs \
        src/agent/OpenAgent.Tests/Installer/ServiceInstallerTests.cs
git commit -m "feat(installer): add ServiceInstaller wrapping sc.exe"
```

---

## Task 9: FirewallRule — netsh wrapper

**Files:**
- Create: `src/agent/OpenAgent/Installer/FirewallRule.cs`
- Create: `src/agent/OpenAgent.Tests/Installer/FirewallRuleTests.cs`

- [ ] **Step 1: Write the failing tests**

Create `src/agent/OpenAgent.Tests/Installer/FirewallRuleTests.cs`:

```csharp
using OpenAgent.Installer;

namespace OpenAgent.Tests.Installer;

public class FirewallRuleTests
{
    [Fact]
    public void Add_ComposesNetshAdvfirewallAddRule()
    {
        var runner = new FakeSystemCommandRunner();
        var rule = new FirewallRule(runner);

        rule.Add(ruleName: "OpenAgent", port: 8080);

        Assert.Single(runner.Calls);
        Assert.Equal(
            ("netsh.exe", "advfirewall firewall add rule name=\"OpenAgent\" dir=in action=allow protocol=TCP localport=8080"),
            runner.Calls[0]);
    }

    [Fact]
    public void Remove_ComposesNetshAdvfirewallDeleteRule()
    {
        var runner = new FakeSystemCommandRunner();
        var rule = new FirewallRule(runner);

        rule.Remove(ruleName: "OpenAgent");

        Assert.Single(runner.Calls);
        Assert.Equal(
            ("netsh.exe", "advfirewall firewall delete rule name=\"OpenAgent\""),
            runner.Calls[0]);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

```bash
cd src/agent && dotnet test --filter "FullyQualifiedName~FirewallRuleTests"
```

Expected: FAIL.

- [ ] **Step 3: Implement FirewallRule**

Create `src/agent/OpenAgent/Installer/FirewallRule.cs`:

```csharp
namespace OpenAgent.Installer;

/// <summary>
/// Manages the inbound Windows Firewall rule that exposes the HTTP port to the LAN.
/// Opt-in: only invoked when --install is run with --open-firewall-port.
/// </summary>
public sealed class FirewallRule
{
    private readonly ISystemCommandRunner _runner;

    public FirewallRule(ISystemCommandRunner runner) => _runner = runner;

    public void Add(string ruleName, int port) =>
        _runner.Run("netsh.exe",
            $"advfirewall firewall add rule name=\"{ruleName}\" dir=in action=allow protocol=TCP localport={port}");

    public void Remove(string ruleName) =>
        _runner.Run("netsh.exe",
            $"advfirewall firewall delete rule name=\"{ruleName}\"");
}
```

- [ ] **Step 4: Run tests to verify they pass**

```bash
cd src/agent && dotnet test --filter "FullyQualifiedName~FirewallRuleTests"
```

Expected: PASS 2 tests.

- [ ] **Step 5: Commit**

```bash
git add src/agent/OpenAgent/Installer/FirewallRule.cs \
        src/agent/OpenAgent.Tests/Installer/FirewallRuleTests.cs
git commit -m "feat(installer): add FirewallRule wrapping netsh advfirewall"
```

---

## Task 10: EventLogRegistrar

**Files:**
- Create: `src/agent/OpenAgent/Installer/EventLogRegistrar.cs`

- [ ] **Step 1: Implement the registrar**

Create `src/agent/OpenAgent/Installer/EventLogRegistrar.cs`:

```csharp
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace OpenAgent.Installer;

/// <summary>
/// Ensures the "OpenAgent" Event Log source is registered in the Application log.
/// Called from --install (admin is required to write the registration).
/// Idempotent: no-op if the source already exists.
/// </summary>
public static class EventLogRegistrar
{
    public const string SourceName = "OpenAgent";
    public const string LogName = "Application";

    /// <summary>Creates the event source if it does not already exist. No-op on non-Windows.</summary>
    public static void Ensure()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return;

        EnsureWindows();
    }

    [SupportedOSPlatform("windows")]
    private static void EnsureWindows()
    {
        if (!EventLog.SourceExists(SourceName))
            EventLog.CreateEventSource(new EventSourceCreationData(SourceName, LogName));
    }
}
```

- [ ] **Step 2: Build**

```bash
cd src/agent && dotnet build
```

Expected: Build succeeds. `EventLog.SourceExists` / `CreateEventSource` are part of `System.Diagnostics.EventLog`, which pulls in via the `Microsoft.Extensions.Logging.EventLog` transitive reference added in Task 1.

- [ ] **Step 3: Commit**

Unit tests would require writing to the registry, which is inappropriate for unit tests; this is covered by the end-to-end QA task.

```bash
git add src/agent/OpenAgent/Installer/EventLogRegistrar.cs
git commit -m "feat(installer): add EventLogRegistrar for lifecycle logging"
```

---

## Task 11: PreInstallChecks

**Files:**
- Create: `src/agent/OpenAgent/Installer/PreInstallChecks.cs`
- Create: `src/agent/OpenAgent.Tests/Installer/PreInstallChecksTests.cs`

- [ ] **Step 1: Write the failing tests**

Create `src/agent/OpenAgent.Tests/Installer/PreInstallChecksTests.cs`:

```csharp
using OpenAgent.Installer;

namespace OpenAgent.Tests.Installer;

public class PreInstallChecksTests : IDisposable
{
    private readonly string _tempDir;

    public PreInstallChecksTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "openagent-precheck-" + Guid.NewGuid());
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    [Fact]
    public void VerifyBridgeScriptPresent_WithMissingFile_ReturnsError()
    {
        var result = PreInstallChecks.VerifyBridgeScriptPresent(_tempDir);

        Assert.False(result.Ok);
        Assert.Contains("node\\baileys-bridge.js", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void VerifyBridgeScriptPresent_WithPresentFile_ReturnsOk()
    {
        var nodeDir = Path.Combine(_tempDir, "node");
        Directory.CreateDirectory(nodeDir);
        File.WriteAllText(Path.Combine(nodeDir, "baileys-bridge.js"), "// stub");

        var result = PreInstallChecks.VerifyBridgeScriptPresent(_tempDir);

        Assert.True(result.Ok);
    }

    [Fact]
    public void VerifyNodeAvailable_WithRunnerSuccess_ReturnsOk()
    {
        var runner = new FakeSystemCommandRunner();
        runner.Responses.Enqueue(new CommandResult(0, "v22.10.0"));

        var result = PreInstallChecks.VerifyNodeAvailable(runner);

        Assert.True(result.Ok);
        Assert.Equal(("node", "--version"), runner.Calls[0]);
    }

    [Fact]
    public void VerifyNodeAvailable_WithRunnerFailure_ReturnsError()
    {
        var runner = new FakeSystemCommandRunner();
        runner.Responses.Enqueue(new CommandResult(9009, "'node' is not recognized"));

        var result = PreInstallChecks.VerifyNodeAvailable(runner);

        Assert.False(result.Ok);
        Assert.Contains("node", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void VerifyPathSafe_WithNewline_ReturnsError()
    {
        var result = PreInstallChecks.VerifyPathSafe("C:\\Open\nAgent");
        Assert.False(result.Ok);
    }

    [Fact]
    public void VerifyPathSafe_WithSpaces_ReturnsOk()
    {
        var result = PreInstallChecks.VerifyPathSafe(@"C:\Program Files\OpenAgent");
        Assert.True(result.Ok);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

```bash
cd src/agent && dotnet test --filter "FullyQualifiedName~PreInstallChecksTests"
```

Expected: FAIL — `PreInstallChecks` does not exist.

- [ ] **Step 3: Implement PreInstallChecks**

Create `src/agent/OpenAgent/Installer/PreInstallChecks.cs`:

```csharp
namespace OpenAgent.Installer;

/// <summary>
/// Validates that the install source folder and the host environment are ready
/// to register the service. Each check returns a CheckResult for the caller
/// to aggregate and surface as a single clear error.
/// </summary>
public static class PreInstallChecks
{
    public static CheckResult VerifyBridgeScriptPresent(string sourceFolder)
    {
        var bridgePath = Path.Combine(sourceFolder, "node", "baileys-bridge.js");
        return File.Exists(bridgePath)
            ? CheckResult.Success
            : new CheckResult(false, $"Expected node\\baileys-bridge.js next to the exe (looked at {bridgePath}). Extract the full distribution before running --install.");
    }

    public static CheckResult VerifyNodeAvailable(ISystemCommandRunner runner)
    {
        CommandResult result;
        try
        {
            result = runner.Run("node", "--version", timeout: TimeSpan.FromSeconds(5));
        }
        catch (Exception ex)
        {
            return new CheckResult(false, $"Failed to invoke 'node --version': {ex.Message}. Install Node.js 22+ from https://nodejs.org and ensure it is on PATH.");
        }

        return result.ExitCode == 0
            ? CheckResult.Success
            : new CheckResult(false, $"'node --version' exited {result.ExitCode}. Install Node.js 22+ from https://nodejs.org and ensure it is on PATH.");
    }

    public static CheckResult VerifyPathSafe(string path)
    {
        foreach (var c in path)
        {
            if (c == '\0' || c == '\n' || c == '\r')
                return new CheckResult(false, $"Install path contains unsupported characters: {path}");
        }
        return CheckResult.Success;
    }
}

public sealed record CheckResult(bool Ok, string Message)
{
    public static CheckResult Success { get; } = new(true, "");
}
```

- [ ] **Step 4: Run tests to verify they pass**

```bash
cd src/agent && dotnet test --filter "FullyQualifiedName~PreInstallChecksTests"
```

Expected: PASS 6 tests.

- [ ] **Step 5: Commit**

```bash
git add src/agent/OpenAgent/Installer/PreInstallChecks.cs \
        src/agent/OpenAgent.Tests/Installer/PreInstallChecksTests.cs
git commit -m "feat(installer): add PreInstallChecks for bridge, Node, path sanity"
```

---

## Task 12: InstallerCli — argument parser + dispatch

**Files:**
- Create: `src/agent/OpenAgent/Installer/InstallerCli.cs`

- [ ] **Step 1: Implement the CLI dispatcher**

Create `src/agent/OpenAgent/Installer/InstallerCli.cs`:

```csharp
using System.Runtime.InteropServices;

namespace OpenAgent.Installer;

/// <summary>
/// Top-of-main dispatcher for install-mode commands. Returns null if the args do not request
/// an install-mode operation and the caller should proceed to normal host startup; otherwise
/// returns the exit code to pass back from Main.
/// </summary>
public static class InstallerCli
{
    public const string ServiceName = "OpenAgent";
    public const string DisplayName = "OpenAgent";
    public const string Description = "Multi-channel AI agent platform";
    public const string DefaultInstallPath = @"C:\OpenAgent";
    public const int DefaultHttpPort = 8080;

    /// <summary>
    /// Inspects args[0] for --install / --uninstall / --restart / --status. Returns the exit code
    /// if a command was handled, or null if args do not trigger an install-mode command.
    /// --service is handled separately by the caller (it configures the host, it doesn't exit).
    /// </summary>
    public static int? TryHandle(string[] args)
    {
        if (args.Length == 0)
            return null;

        return args[0] switch
        {
            "--install"   => Install(args),
            "--uninstall" => Uninstall(),
            "--restart"   => Restart(),
            "--status"    => Status(),
            _             => null
        };
    }

    private static int Install(string[] args)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            Console.Error.WriteLine("--install is only supported on Windows.");
            return 1;
        }

        var adminCheck = ElevationCheck.RequireAdministrator("--install");
        if (adminCheck != 0)
            return adminCheck;

        var installPath = ParseOptionalArg(args, "--path") ?? DefaultInstallPath;
        var openFirewall = args.Contains("--open-firewall-port");

        var runner = new SystemCommandRunner();

        // Pre-install checks
        var sourceFolder = AppContext.BaseDirectory;
        foreach (var check in new[]
        {
            PreInstallChecks.VerifyBridgeScriptPresent(sourceFolder),
            PreInstallChecks.VerifyNodeAvailable(runner),
            PreInstallChecks.VerifyPathSafe(installPath)
        })
        {
            if (!check.Ok)
            {
                Console.Error.WriteLine(check.Message);
                return 1;
            }
        }

        var installer = new ServiceInstaller(runner);
        var reinstall = installer.IsInstalled(ServiceName);

        if (reinstall)
        {
            Console.WriteLine($"Existing service found; stopping for upgrade.");
            installer.Stop(ServiceName);
            Thread.Sleep(TimeSpan.FromSeconds(2));
        }

        // Copy source folder contents to install path (binary + node/, skip any data folders that might be present)
        Directory.CreateDirectory(installPath);
        CopyInstallArtifacts(sourceFolder, installPath);

        var exePath = Path.Combine(installPath, "OpenAgent.exe");

        if (reinstall)
        {
            installer.UpdateBinPath(ServiceName, exePath);
        }
        else
        {
            installer.Create(ServiceName, exePath, DisplayName, Description);
        }

        EventLogRegistrar.Ensure();

        if (openFirewall)
            new FirewallRule(runner).Add(ServiceName, DefaultHttpPort);

        installer.Start(ServiceName);

        Console.WriteLine($"OpenAgent {(reinstall ? "upgraded" : "installed")} at {installPath} and started.");
        return 0;
    }

    private static int Uninstall()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            Console.Error.WriteLine("--uninstall is only supported on Windows.");
            return 1;
        }

        var adminCheck = ElevationCheck.RequireAdministrator("--uninstall");
        if (adminCheck != 0)
            return adminCheck;

        var runner = new SystemCommandRunner();
        var installer = new ServiceInstaller(runner);

        if (!installer.IsInstalled(ServiceName))
        {
            Console.Error.WriteLine($"Service {ServiceName} is not installed.");
            return 0;
        }

        installer.Stop(ServiceName);
        Thread.Sleep(TimeSpan.FromSeconds(2));
        installer.Delete(ServiceName);
        new FirewallRule(runner).Remove(ServiceName);

        Console.WriteLine($"{ServiceName} uninstalled. Config, logs, and database preserved.");
        return 0;
    }

    private static int Restart()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            Console.Error.WriteLine("--restart is only supported on Windows.");
            return 1;
        }

        var adminCheck = ElevationCheck.RequireAdministrator("--restart");
        if (adminCheck != 0)
            return adminCheck;

        var installer = new ServiceInstaller(new SystemCommandRunner());
        installer.Stop(ServiceName);
        Thread.Sleep(TimeSpan.FromSeconds(2));
        installer.Start(ServiceName);

        Console.WriteLine($"{ServiceName} restarted.");
        return 0;
    }

    private static int Status()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            Console.Error.WriteLine("--status is only supported on Windows.");
            return 1;
        }

        var runner = new SystemCommandRunner();
        var installer = new ServiceInstaller(runner);
        var installed = installer.IsInstalled(ServiceName);

        Console.WriteLine($"Service installed: {installed}");

        if (installed)
        {
            var result = runner.Run("sc.exe", $"query {ServiceName}");
            Console.WriteLine(result.Output);
        }

        return 0;
    }

    private static string? ParseOptionalArg(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == name)
                return args[i + 1];
        }
        return null;
    }

    private static void CopyInstallArtifacts(string sourceFolder, string installPath)
    {
        // Copy the exe itself
        var sourceExe = Path.Combine(sourceFolder, "OpenAgent.exe");
        if (File.Exists(sourceExe))
            File.Copy(sourceExe, Path.Combine(installPath, "OpenAgent.exe"), overwrite: true);

        // Copy any .dll / .pdb / .json adjacent to the exe (appsettings, etc.)
        foreach (var file in Directory.EnumerateFiles(sourceFolder, "*", SearchOption.TopDirectoryOnly))
        {
            var name = Path.GetFileName(file);
            if (name.Equals("OpenAgent.exe", StringComparison.OrdinalIgnoreCase))
                continue;
            File.Copy(file, Path.Combine(installPath, name), overwrite: true);
        }

        // Copy the node/ directory recursively
        var sourceNode = Path.Combine(sourceFolder, "node");
        if (Directory.Exists(sourceNode))
        {
            var destNode = Path.Combine(installPath, "node");
            CopyDirectory(sourceNode, destNode);
        }
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source))
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: true);
        foreach (var dir in Directory.EnumerateDirectories(source))
            CopyDirectory(dir, Path.Combine(destination, Path.GetFileName(dir)));
    }
}
```

- [ ] **Step 2: Build**

```bash
cd src/agent && dotnet build
```

Expected: Build succeeds.

- [ ] **Step 3: Commit**

Integration-testing the full install flow requires admin privileges and modifies the host machine; this is covered by the manual QA in Task 15. The pieces used here (`PreInstallChecks`, `ServiceInstaller`, `FirewallRule`) are already covered by their own unit tests.

```bash
git add src/agent/OpenAgent/Installer/InstallerCli.cs
git commit -m "feat(installer): add CLI dispatcher for install/uninstall/restart/status"
```

---

## Task 13: Program.cs — wire install-mode dispatch and Windows service mode

**Files:**
- Modify: `src/agent/OpenAgent/Program.cs`

- [ ] **Step 1: Add the early-exit dispatch and service mode**

Modify `src/agent/OpenAgent/Program.cs`. At the very top of the file (after the `using` block, before `var builder = ...`), insert:

```csharp
// Install-mode dispatch — runs before the web host is built.
// Returns an exit code if args match --install / --uninstall / --restart / --status; otherwise proceeds.
var installerExit = OpenAgent.Installer.InstallerCli.TryHandle(args);
if (installerExit.HasValue)
    return installerExit.Value;
```

Then modify the `WebApplication.CreateBuilder(args)` line — it's already there; no change — but add Windows service + Event Log configuration conditionally. Replace the `var builder = WebApplication.CreateBuilder(args);` line with:

```csharp
var builder = WebApplication.CreateBuilder(args);

var runningAsService = args.Contains("--service");
if (runningAsService)
{
    builder.Host.UseWindowsService(options => options.ServiceName = OpenAgent.Installer.InstallerCli.ServiceName);
    if (OperatingSystem.IsWindows())
    {
        builder.Logging.AddEventLog(options => options.SourceName = OpenAgent.Installer.EventLogRegistrar.SourceName);
    }
}
```

(Because `Program.cs` currently uses top-level statements and the file lacks an explicit `Main`, wrapping the `return` requires the compiler to treat top-level statements as returning an int. The `return 0` at the bottom of the current file would be added — see Step 2.)

- [ ] **Step 2: Ensure Program returns int**

At the **very end** of `src/agent/OpenAgent/Program.cs`, after `app.Run();`, add:

```csharp

return 0;
```

Top-level-statement programs infer a return type of `int` from the presence of a `return` statement with an int; `app.Run()` blocks until shutdown and then returns naturally, so the `return 0;` is only reached on clean shutdown.

- [ ] **Step 3: Build**

```bash
cd src/agent && dotnet build
```

Expected: Build succeeds.

- [ ] **Step 4: Run the full test suite**

```bash
cd src/agent && dotnet test
```

Expected: All tests pass, including existing integration tests. No regressions introduced by the install-mode dispatch (it returns `null` when args don't match).

- [ ] **Step 5: Smoke-test console mode locally**

```bash
cd src/agent && dotnet run --project OpenAgent
```

Expected: The agent starts, logs "Bootstrap — ensure required folders..." and listens on its configured port. Ctrl+C stops it cleanly. Kills the process and continues.

- [ ] **Step 6: Commit**

```bash
git add src/agent/OpenAgent/Program.cs
git commit -m "feat: wire install-mode CLI and Windows service mode into Program.cs"
```

---

## Task 14: Agent-side symlink awareness (error hints + system prompt block)

**Files:**
- Modify: `src/agent/OpenAgent.Tools.FileSystem/FileReadTool.cs`
- Modify: `src/agent/OpenAgent.Tools.FileSystem/FileWriteTool.cs`
- Modify: `src/agent/OpenAgent.Tools.FileSystem/FileAppendTool.cs`
- Modify: `src/agent/OpenAgent.Tools.FileSystem/FileEditTool.cs`
- Modify: `src/agent/OpenAgent/SystemPromptBuilder.cs`

**Spec reference:** "the error message must hint at the root-relative convention so the model can self-correct" + "The system prompt must state the convention explicitly." Both apply only when symlinks exist under the data root — deployments without symlinks see no behavior change.

- [ ] **Step 1: Write the failing test for FileReadTool error hint**

Append to `src/agent/OpenAgent.Tests/` in a new file `FileSystemErrorMessageTests.cs`:

```csharp
using OpenAgent.Tools.FileSystem;
using System.Text;
using System.Text.Json;

namespace OpenAgent.Tests;

public class FileSystemErrorMessageTests : IDisposable
{
    private readonly string _tempDir;

    public FileSystemErrorMessageTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "openagent-fs-err-" + Guid.NewGuid());
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    [Fact]
    public async Task FileRead_OutsideBase_WithSymlinkRoots_IncludesHintsInError()
    {
        // Create a junction-like structure: a real subdirectory simulating a configured link name
        var target = Path.Combine(_tempDir, "_target_media");
        Directory.CreateDirectory(target);
        var linkPath = Path.Combine(_tempDir, "media");
        Directory.CreateSymbolicLink(linkPath, target);

        var tool = new FileReadTool(_tempDir, Encoding.UTF8);
        var result = await tool.ExecuteAsync("""{"path":"../escape.txt"}""", "conv-1");

        using var doc = JsonDocument.Parse(result);
        var error = doc.RootElement.GetProperty("error").GetString();
        Assert.Contains("outside", error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("media", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task FileRead_OutsideBase_WithoutSymlinkRoots_RetainsGenericError()
    {
        var tool = new FileReadTool(_tempDir, Encoding.UTF8);
        var result = await tool.ExecuteAsync("""{"path":"../escape.txt"}""", "conv-1");

        using var doc = JsonDocument.Parse(result);
        var error = doc.RootElement.GetProperty("error").GetString();
        Assert.Contains("outside", error, StringComparison.OrdinalIgnoreCase);
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

```bash
cd src/agent && dotnet test --filter "FullyQualifiedName~FileSystemErrorMessageTests"
```

Expected: FAIL on the "includes hints" test — current error is `"path is outside allowed directory"` with no hint.

- [ ] **Step 3: Add a shared helper for listing symlink roots**

Create `src/agent/OpenAgent.Tools.FileSystem/SymlinkRoots.cs`:

```csharp
namespace OpenAgent.Tools.FileSystem;

/// <summary>
/// Enumerates the names of top-level directories under the data root that are reparse points
/// (directory junctions on Windows, symlinks on Linux). Used to produce path-hint error messages
/// and system-prompt guidance. Returns an empty list on deployments without configured symlinks.
/// </summary>
internal static class SymlinkRoots
{
    public static IReadOnlyList<string> List(string dataPath)
    {
        if (!Directory.Exists(dataPath))
            return Array.Empty<string>();

        var names = new List<string>();
        foreach (var dir in Directory.EnumerateDirectories(dataPath, "*", SearchOption.TopDirectoryOnly))
        {
            var info = new DirectoryInfo(dir);
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
                names.Add(info.Name);
        }
        names.Sort(StringComparer.Ordinal);
        return names;
    }
}
```

- [ ] **Step 4: Update all four file tools to use the hint**

In each of `FileReadTool.cs`, `FileWriteTool.cs`, `FileAppendTool.cs`, `FileEditTool.cs`, replace the line

```csharp
            return JsonSerializer.Serialize(new { error = "path is outside allowed directory" });
```

with:

```csharp
            var roots = SymlinkRoots.List(basePath);
            var hint = roots.Count > 0
                ? $"path is outside allowed directory. Configured mount points: {string.Join(", ", roots.Select(r => r + "/"))} — use one of these prefixes for external paths."
                : "path is outside allowed directory";
            return JsonSerializer.Serialize(new { error = hint });
```

- [ ] **Step 5: Run tests to verify error-hint tests pass**

```bash
cd src/agent && dotnet test --filter "FullyQualifiedName~FileSystemErrorMessageTests"
```

Expected: PASS 2 tests.

- [ ] **Step 6: Write the failing test for SystemPromptBuilder symlink block**

Extend or create `src/agent/OpenAgent.Tests/SystemPromptBuilderTests.cs` — add a test that a configured symlink produces a `<path_conventions>` block in the built system prompt.

```csharp
using Microsoft.Extensions.Logging.Abstractions;
using OpenAgent;
using OpenAgent.Models.Configs;
using OpenAgent.Models.Conversations;
using OpenAgent.Skills;

namespace OpenAgent.Tests;

public class SystemPromptSymlinkBlockTests : IDisposable
{
    private readonly string _tempDir;

    public SystemPromptSymlinkBlockTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "openagent-sp-sym-" + Guid.NewGuid());
        Directory.CreateDirectory(_tempDir);

        // Minimum personality file so the builder has something to read
        File.WriteAllText(Path.Combine(_tempDir, "AGENTS.md"), "# You are the agent.");
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    [Fact]
    public void Build_WithSymlinkRoots_IncludesPathConventionsBlock()
    {
        var target = Path.Combine(_tempDir, "_target");
        Directory.CreateDirectory(target);
        Directory.CreateSymbolicLink(Path.Combine(_tempDir, "media"), target);

        var builder = new SystemPromptBuilder(
            new AgentEnvironment { DataPath = _tempDir },
            new SkillCatalog(Path.Combine(_tempDir, "skills"), NullLogger<SkillCatalog>.Instance),
            new AgentConfig(),
            NullLogger<SystemPromptBuilder>.Instance);

        var prompt = builder.Build(ConversationType.Text);

        Assert.Contains("<path_conventions>", prompt);
        Assert.Contains("media/", prompt);
    }

    [Fact]
    public void Build_WithoutSymlinkRoots_OmitsPathConventionsBlock()
    {
        var builder = new SystemPromptBuilder(
            new AgentEnvironment { DataPath = _tempDir },
            new SkillCatalog(Path.Combine(_tempDir, "skills"), NullLogger<SkillCatalog>.Instance),
            new AgentConfig(),
            NullLogger<SystemPromptBuilder>.Instance);

        var prompt = builder.Build(ConversationType.Text);

        Assert.DoesNotContain("<path_conventions>", prompt);
    }
}
```

- [ ] **Step 7: Run tests to verify they fail**

```bash
cd src/agent && dotnet test --filter "FullyQualifiedName~SystemPromptSymlinkBlockTests"
```

Expected: FAIL on the "includes block" test.

- [ ] **Step 8: Update SystemPromptBuilder.Build**

In `src/agent/OpenAgent/SystemPromptBuilder.cs`, after the loop that concatenates file contents and after the memory block but before the skill block (or find an appropriate point — just before the method returns the joined string), add a path-conventions section:

```csharp
        var symlinkRoots = EnumerateTopLevelReparsePoints(_dataPath);
        if (symlinkRoots.Count > 0)
        {
            var lines = symlinkRoots.Select(name =>
                $"  - {name}/... — reaches a mounted external path; shell output may show real paths under this mount, translate to the short form when passing to file tools.");
            sections.Add(
                "<path_conventions>\n" +
                "When passing paths to file tools, use paths relative to the agent's root. Configured mount points:\n" +
                string.Join("\n", lines) + "\n" +
                "</path_conventions>");
        }
```

Add this helper at the bottom of the class:

```csharp
    private static List<string> EnumerateTopLevelReparsePoints(string dataPath)
    {
        var names = new List<string>();
        if (!Directory.Exists(dataPath))
            return names;

        foreach (var dir in Directory.EnumerateDirectories(dataPath, "*", SearchOption.TopDirectoryOnly))
        {
            var info = new DirectoryInfo(dir);
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
                names.Add(info.Name);
        }
        names.Sort(StringComparer.Ordinal);
        return names;
    }
```

- [ ] **Step 9: Run tests to verify both pass**

```bash
cd src/agent && dotnet test --filter "FullyQualifiedName~SystemPromptSymlinkBlockTests"
```

Expected: PASS 2 tests.

- [ ] **Step 10: Run full test suite to check for regressions**

```bash
cd src/agent && dotnet test
```

Expected: All tests pass.

- [ ] **Step 11: Commit**

```bash
git add src/agent/OpenAgent.Tools.FileSystem/SymlinkRoots.cs \
        src/agent/OpenAgent.Tools.FileSystem/FileReadTool.cs \
        src/agent/OpenAgent.Tools.FileSystem/FileWriteTool.cs \
        src/agent/OpenAgent.Tools.FileSystem/FileAppendTool.cs \
        src/agent/OpenAgent.Tools.FileSystem/FileEditTool.cs \
        src/agent/OpenAgent/SystemPromptBuilder.cs \
        src/agent/OpenAgent.Tests/FileSystemErrorMessageTests.cs \
        src/agent/OpenAgent.Tests/SystemPromptSymlinkBlockTests.cs
git commit -m "feat: agent-side symlink awareness — error hints and prompt block"
```

---

## Task 15: End-to-end publish + manual QA

**Files:**
- (No new code; this task produces a published artifact and walks a manual smoke test.)

- [ ] **Step 1: Publish the Windows self-contained single-file exe**

```bash
cd src/agent/OpenAgent && dotnet publish -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o ../../../publish/win-x64
```

Expected: `publish/win-x64/OpenAgent.exe` (~70-90 MB) exists. Size within expectation. No build errors.

- [ ] **Step 2: Stage the node bridge next to the exe**

Copy `src/agent/OpenAgent.Channel.WhatsApp/node/` into `publish/win-x64/node/` so the distribution folder layout matches:

```
publish/win-x64/
  OpenAgent.exe
  appsettings.json
  node/
    baileys-bridge.js
    node_modules/
    package.json
```

(If the WhatsApp project already copies `node/` as content during build, verify that's happening; otherwise copy manually once or add a build step — not in scope for this task, just make the smoke test runnable.)

- [ ] **Step 3: Manual QA — non-admin rejection**

Open a non-elevated Command Prompt and run:

```cmd
cd publish\win-x64
OpenAgent.exe --install
```

Expected: Error message "`--install` requires administrator privileges. Run this command from an elevated (\"Run as Administrator\") prompt." and exit code 5.

- [ ] **Step 4: Manual QA — install**

Open an **elevated** Command Prompt and run:

```cmd
cd publish\win-x64
OpenAgent.exe --install
```

Expected:
- Pre-install checks pass.
- `C:\OpenAgent\` created with `OpenAgent.exe`, `appsettings.json`, `node\` copied in.
- Service `OpenAgent` registered with `sc query OpenAgent` returning `STATE: 4 RUNNING`.
- `services.msc` shows `OpenAgent` with Description "Multi-channel AI agent platform" and startup type "Automatic".
- First run creates `C:\OpenAgent\config\agent.json` with content `{"symlinks": {}}`.
- `http://localhost:8080/health` returns 200.
- Windows Event Log → Application shows `OpenAgent` source entries for service start.

- [ ] **Step 5: Manual QA — configure a symlink, restart**

Edit `C:\OpenAgent\config\agent.json` to add a symlink entry (use a path that actually exists on your machine):

```json
{
  "symlinks": {
    "media": "D:\\Media"
  }
}
```

From an elevated prompt:

```cmd
OpenAgent.exe --restart
```

Expected: `C:\OpenAgent\media` now exists as a directory junction pointing at `D:\Media`. `dir /A:L C:\OpenAgent` shows `<JUNCTION> media [D:\Media]`.

- [ ] **Step 6: Manual QA — upgrade (re-install over existing)**

From the publish folder, re-run install in an elevated prompt:

```cmd
OpenAgent.exe --install
```

Expected:
- "Existing service found; stopping for upgrade." message.
- Service stops, files replaced, service starts.
- `config\agent.json`, `conversations.db`, `media` junction all preserved.
- `--status` shows service running.

- [ ] **Step 7: Manual QA — uninstall**

```cmd
OpenAgent.exe --uninstall
```

Expected:
- Service stopped and deleted (`sc query OpenAgent` → `1060 The specified service does not exist`).
- `C:\OpenAgent\config\`, `C:\OpenAgent\conversations.db`, `C:\OpenAgent\media` (junction) all **still present**.
- Firewall rule (if opened) removed.

- [ ] **Step 8: Document findings**

Any surprises, edge cases, or docs updates needed from the manual QA go into a follow-up issue. Commit any small fixes discovered during smoke-testing.

- [ ] **Step 9: Final commit**

No code changes here if everything passed. If smoke-testing revealed fixable issues, commit those before wrapping the feature.

```bash
git log --oneline | head -20
```

Verify the commit history tells a clean story from Task 1 to Task 15.

---

## Verification Summary

| Concern | Where covered |
|---|---|
| `DATA_DIR` env var still wins when set | Task 2 (`RootResolverTests`) |
| `AppContext.BaseDirectory` fallback for Windows service | Task 2 (`RootResolverTests`) |
| Default `agent.json` seeds `{"symlinks": {}}` | Task 3 (`DataDirectoryBootstrapTests`) |
| Junction created from agent.json `symlinks` block | Task 6 (`DataDirectoryBootstrapTests`) |
| Idempotent symlink creation (re-run safe) | Task 6 |
| Real directory at link path warns + skips | Task 6 |
| Junction via `mklink /J` on Windows, symlink on Linux | Task 5 (`DirectoryLinkCreatorTests`) |
| `sc create` with quote-escaped binPath | Task 8 (`ServiceInstallerTests`) |
| `sc failure` recovery actions | Task 8 |
| `sc config` for path update on re-install | Task 8 |
| `netsh` add/delete firewall rule | Task 9 (`FirewallRuleTests`) |
| Node.js + bridge script pre-install checks | Task 11 (`PreInstallChecksTests`) |
| Path sanity (newline/null rejection) | Task 11 |
| `WindowsPrincipal.IsInRole(Administrator)` elevation | Task 7 (manual verification, Task 15 Step 3) |
| Event Log source registered on install | Task 10 + Task 15 Step 4 |
| Install-mode CLI dispatch | Task 12 + Task 13 |
| `AddWindowsService()` + `AddEventLog` in `--service` mode | Task 13 + Task 15 Step 4 |
| Path-rejection error hint for symlink roots | Task 14 (`FileSystemErrorMessageTests`) |
| System-prompt `<path_conventions>` block | Task 14 (`SystemPromptSymlinkBlockTests`) |
| End-to-end install / restart / uninstall | Task 15 |
