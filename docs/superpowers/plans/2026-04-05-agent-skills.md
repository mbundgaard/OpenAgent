# Agent Skills Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add Agent Skills support to OpenAgent, implementing the open [agentskills.io](https://agentskills.io/specification) specification so the agent can discover, catalog, and activate skill instruction documents at runtime.

**Architecture:** Skills are markdown instruction files (`SKILL.md`) discovered from `{dataPath}/skills/`. At session start, only name + description are injected into the system prompt (~50-100 tokens each). When the agent decides a skill is relevant, it activates it by reading the full SKILL.md via the existing `file_read` tool or a dedicated `activate_skill` tool. This follows the spec's three-tier progressive disclosure model. A new `OpenAgent.Skills` project handles discovery and parsing; `SystemPromptBuilder` is extended to inject the skill catalog; a `SkillToolHandler` provides the dedicated activation tool.

**Tech Stack:** .NET 10, System.Text.Json, xUnit

**Reference:** [Agent Skills Specification](https://agentskills.io/specification), [Client Implementation Guide](https://agentskills.io/client-implementation/adding-skills-support)

---

## File Structure

| Action | Path | Responsibility |
|--------|------|----------------|
| Create | `src/agent/OpenAgent.Skills/OpenAgent.Skills.csproj` | Project file — references OpenAgent.Contracts |
| Create | `src/agent/OpenAgent.Skills/SkillEntry.cs` | Parsed skill record (name, description, location, body, metadata) |
| Create | `src/agent/OpenAgent.Skills/SkillFrontmatterParser.cs` | YAML frontmatter extraction from SKILL.md files |
| Create | `src/agent/OpenAgent.Skills/SkillDiscovery.cs` | Scans skill directories, returns list of SkillEntry |
| Create | `src/agent/OpenAgent.Skills/SkillCatalog.cs` | Holds discovered skills, builds prompt XML, provides lookup |
| Create | `src/agent/OpenAgent.Skills/SkillToolHandler.cs` | IToolHandler with `activate_skill` tool |
| Modify | `src/agent/OpenAgent/SystemPromptBuilder.cs` | Inject `<available_skills>` catalog after existing file sections |
| Modify | `src/agent/OpenAgent/DataDirectoryBootstrap.cs` | Add `skills` to RequiredDirectories |
| Modify | `src/agent/OpenAgent/Program.cs` | Register SkillCatalog and SkillToolHandler in DI |
| Modify | `src/agent/OpenAgent/OpenAgent.csproj` | Add ProjectReference to OpenAgent.Skills |
| Modify | `src/agent/OpenAgent.Tests/OpenAgent.Tests.csproj` | Add ProjectReference to OpenAgent.Skills |
| Create | `src/agent/OpenAgent.Tests/Skills/SkillFrontmatterParserTests.cs` | Unit tests for YAML parsing |
| Create | `src/agent/OpenAgent.Tests/Skills/SkillDiscoveryTests.cs` | Unit tests for directory scanning |
| Create | `src/agent/OpenAgent.Tests/Skills/SkillCatalogTests.cs` | Unit tests for catalog building and lookup |
| Create | `src/agent/OpenAgent.Tests/Skills/SkillToolHandlerTests.cs` | Unit tests for activate_skill tool |

---

### Task 1: Project Setup and SkillEntry Model

**Files:**
- Create: `src/agent/OpenAgent.Skills/OpenAgent.Skills.csproj`
- Create: `src/agent/OpenAgent.Skills/SkillEntry.cs`

- [ ] **Step 1: Create project file**

```xml
<!-- src/agent/OpenAgent.Skills/OpenAgent.Skills.csproj -->
<Project Sdk="Microsoft.NET.Sdk">

  <ItemGroup>
    <ProjectReference Include="..\OpenAgent.Contracts\OpenAgent.Contracts.csproj" />
  </ItemGroup>

</Project>
```

Note: `TargetFramework`, `Nullable`, and `ImplicitUsings` are inherited from `Directory.Build.props`.

- [ ] **Step 2: Create SkillEntry record**

```csharp
// src/agent/OpenAgent.Skills/SkillEntry.cs
namespace OpenAgent.Skills;

/// <summary>
/// A discovered skill parsed from a SKILL.md file.
/// Holds the frontmatter metadata and the body content separately
/// to support progressive disclosure (catalog vs full activation).
/// </summary>
public sealed class SkillEntry
{
    /// <summary>Unique identifier from frontmatter (lowercase, hyphens). Max 64 chars.</summary>
    public required string Name { get; init; }

    /// <summary>What the skill does and when to use it. Max 1024 chars.</summary>
    public required string Description { get; init; }

    /// <summary>Absolute path to the SKILL.md file.</summary>
    public required string Location { get; init; }

    /// <summary>Markdown body content after the YAML frontmatter.</summary>
    public required string Body { get; init; }

    /// <summary>Optional license field from frontmatter.</summary>
    public string? License { get; init; }

    /// <summary>Optional compatibility notes from frontmatter.</summary>
    public string? Compatibility { get; init; }

    /// <summary>Optional arbitrary key-value metadata from frontmatter.</summary>
    public IReadOnlyDictionary<string, string>? Metadata { get; init; }
}
```

- [ ] **Step 3: Add project to solution**

Run: `cd src/agent && dotnet sln add OpenAgent.Skills/OpenAgent.Skills.csproj`
Expected: `Project 'OpenAgent.Skills\OpenAgent.Skills.csproj' added to the solution.`

- [ ] **Step 4: Verify it builds**

Run: `cd src/agent && dotnet build OpenAgent.Skills`
Expected: `Build succeeded. 0 Warning(s) 0 Error(s)`

- [ ] **Step 5: Commit**

```bash
git add src/agent/OpenAgent.Skills/OpenAgent.Skills.csproj src/agent/OpenAgent.Skills/SkillEntry.cs src/agent/OpenAgent.sln
git commit -m "feat(skills): add OpenAgent.Skills project with SkillEntry model"
```

---

### Task 2: YAML Frontmatter Parser

**Files:**
- Create: `src/agent/OpenAgent.Skills/SkillFrontmatterParser.cs`
- Create: `src/agent/OpenAgent.Tests/Skills/SkillFrontmatterParserTests.cs`
- Modify: `src/agent/OpenAgent.Tests/OpenAgent.Tests.csproj` — add ProjectReference to OpenAgent.Skills

- [ ] **Step 1: Add test project reference**

Add to `src/agent/OpenAgent.Tests/OpenAgent.Tests.csproj` inside the `<ItemGroup>` that has other ProjectReferences:

```xml
<ProjectReference Include="..\OpenAgent.Skills\OpenAgent.Skills.csproj" />
```

- [ ] **Step 2: Write failing tests for frontmatter parsing**

```csharp
// src/agent/OpenAgent.Tests/Skills/SkillFrontmatterParserTests.cs
using OpenAgent.Skills;

namespace OpenAgent.Tests.Skills;

public class SkillFrontmatterParserTests
{
    [Fact]
    public void Parse_extracts_required_fields()
    {
        var content = """
            ---
            name: pdf-processing
            description: Extract PDF text, fill forms, merge files.
            ---
            # PDF Processing
            Step-by-step instructions here.
            """;

        var result = SkillFrontmatterParser.Parse(content);

        Assert.True(result.IsValid);
        Assert.Equal("pdf-processing", result.Name);
        Assert.Equal("Extract PDF text, fill forms, merge files.", result.Description);
        Assert.Equal("# PDF Processing\nStep-by-step instructions here.", result.Body);
    }

    [Fact]
    public void Parse_extracts_optional_fields()
    {
        var content = """
            ---
            name: data-analysis
            description: Analyze datasets and generate charts.
            license: Apache-2.0
            compatibility: Requires Python 3.14+
            metadata:
              author: example-org
              version: "1.0"
            ---
            Body content.
            """;

        var result = SkillFrontmatterParser.Parse(content);

        Assert.True(result.IsValid);
        Assert.Equal("Apache-2.0", result.License);
        Assert.Equal("Requires Python 3.14+", result.Compatibility);
        Assert.NotNull(result.Metadata);
        Assert.Equal("example-org", result.Metadata!["author"]);
        Assert.Equal("1.0", result.Metadata["version"]);
    }

    [Fact]
    public void Parse_returns_invalid_when_no_frontmatter()
    {
        var content = "# Just markdown, no frontmatter";

        var result = SkillFrontmatterParser.Parse(content);

        Assert.False(result.IsValid);
        Assert.Contains("frontmatter", result.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_returns_invalid_when_name_missing()
    {
        var content = """
            ---
            description: Does something.
            ---
            Body.
            """;

        var result = SkillFrontmatterParser.Parse(content);

        Assert.False(result.IsValid);
    }

    [Fact]
    public void Parse_returns_invalid_when_description_missing()
    {
        var content = """
            ---
            name: my-skill
            ---
            Body.
            """;

        var result = SkillFrontmatterParser.Parse(content);

        Assert.False(result.IsValid);
    }

    [Fact]
    public void Parse_handles_description_with_colons_leniently()
    {
        // Per agentskills.io client guide: handle unquoted colons in YAML values
        var content = """
            ---
            name: my-skill
            description: Use this skill when: the user asks about PDFs
            ---
            Body.
            """;

        var result = SkillFrontmatterParser.Parse(content);

        Assert.True(result.IsValid);
        Assert.Equal("Use this skill when: the user asks about PDFs", result.Description);
    }

    [Fact]
    public void Parse_trims_body_whitespace()
    {
        var content = """
            ---
            name: my-skill
            description: Does something.
            ---

            Body with leading/trailing whitespace.

            """;

        var result = SkillFrontmatterParser.Parse(content);

        Assert.True(result.IsValid);
        Assert.Equal("Body with leading/trailing whitespace.", result.Body);
    }

    [Fact]
    public void Parse_handles_empty_body()
    {
        var content = """
            ---
            name: my-skill
            description: Does something.
            ---
            """;

        var result = SkillFrontmatterParser.Parse(content);

        Assert.True(result.IsValid);
        Assert.Equal("", result.Body);
    }
}
```

- [ ] **Step 3: Run tests to verify they fail**

Run: `cd src/agent && dotnet test OpenAgent.Tests --filter "FullyQualifiedName~SkillFrontmatterParserTests" --no-restore`
Expected: Build failure — `SkillFrontmatterParser` does not exist yet.

- [ ] **Step 4: Implement the frontmatter parser**

The parser uses simple string splitting — no YAML library dependency. The Agent Skills spec frontmatter is simple enough (flat key-value with one nested `metadata` map) that we can parse it reliably without a full YAML parser.

```csharp
// src/agent/OpenAgent.Skills/SkillFrontmatterParser.cs
namespace OpenAgent.Skills;

/// <summary>
/// Extracts YAML frontmatter and markdown body from a SKILL.md file.
/// Uses simple line-based parsing — no external YAML library needed for the
/// flat key-value structure defined by the Agent Skills spec.
/// </summary>
public static class SkillFrontmatterParser
{
    /// <summary>
    /// Parses a SKILL.md file's content into frontmatter fields and body.
    /// Returns a result with IsValid=false if required fields are missing.
    /// Follows lenient validation per the agentskills.io client implementation guide.
    /// </summary>
    public static FrontmatterParseResult Parse(string content)
    {
        // Normalize line endings
        content = content.Replace("\r\n", "\n").TrimStart();

        // Find frontmatter delimiters
        if (!content.StartsWith("---"))
            return FrontmatterParseResult.Invalid("No YAML frontmatter found (missing opening ---)");

        var endIndex = content.IndexOf("\n---", 3, StringComparison.Ordinal);
        if (endIndex < 0)
            return FrontmatterParseResult.Invalid("No YAML frontmatter found (missing closing ---)");

        var yamlBlock = content[3..endIndex].Trim();
        var body = content[(endIndex + 4)..].Trim();

        // Parse YAML lines into key-value pairs
        var fields = ParseYamlBlock(yamlBlock);

        // Required fields
        if (!fields.TryGetValue("name", out var name) || string.IsNullOrWhiteSpace(name))
            return FrontmatterParseResult.Invalid("Required field 'name' is missing or empty");

        if (!fields.TryGetValue("description", out var description) || string.IsNullOrWhiteSpace(description))
            return FrontmatterParseResult.Invalid("Required field 'description' is missing or empty");

        // Optional fields
        fields.TryGetValue("license", out var license);
        fields.TryGetValue("compatibility", out var compatibility);

        // Parse metadata (nested key-value map)
        Dictionary<string, string>? metadata = null;
        if (fields.TryGetValue("__metadata_block", out var metadataBlock))
        {
            metadata = ParseMetadataBlock(metadataBlock);
        }

        return new FrontmatterParseResult
        {
            IsValid = true,
            Name = name.Trim(),
            Description = description.Trim(),
            Body = body,
            License = license?.Trim(),
            Compatibility = compatibility?.Trim(),
            Metadata = metadata
        };
    }

    /// <summary>
    /// Parses a simple YAML block into a dictionary. Handles top-level scalar fields
    /// and the nested 'metadata' map. Colons inside values are preserved (lenient parsing).
    /// </summary>
    private static Dictionary<string, string> ParseYamlBlock(string yaml)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var lines = yaml.Split('\n');
        var metadataLines = new List<string>();
        var inMetadata = false;

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];

            // Detect metadata block (indented lines after "metadata:")
            if (inMetadata)
            {
                if (line.Length > 0 && (line[0] == ' ' || line[0] == '\t'))
                {
                    metadataLines.Add(line);
                    continue;
                }
                // End of metadata block
                inMetadata = false;
                result["__metadata_block"] = string.Join("\n", metadataLines);
            }

            // Skip empty lines and comments
            var trimmed = line.TrimStart();
            if (trimmed.Length == 0 || trimmed[0] == '#')
                continue;

            // Find the first colon that separates key from value
            var colonIndex = trimmed.IndexOf(':');
            if (colonIndex < 0)
                continue;

            var key = trimmed[..colonIndex].Trim();
            var value = trimmed[(colonIndex + 1)..].Trim();

            if (key.Equals("metadata", StringComparison.OrdinalIgnoreCase) && value.Length == 0)
            {
                inMetadata = true;
                continue;
            }

            result[key] = value;
        }

        // Flush trailing metadata block
        if (inMetadata && metadataLines.Count > 0)
            result["__metadata_block"] = string.Join("\n", metadataLines);

        return result;
    }

    /// <summary>Parses indented metadata lines into a string-string dictionary.</summary>
    private static Dictionary<string, string> ParseMetadataBlock(string block)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var lines = block.Split('\n');

        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed[0] == '#')
                continue;

            var colonIndex = trimmed.IndexOf(':');
            if (colonIndex < 0)
                continue;

            var key = trimmed[..colonIndex].Trim();
            var value = trimmed[(colonIndex + 1)..].Trim();

            // Strip surrounding quotes
            if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
                value = value[1..^1];

            result[key] = value;
        }

        return result;
    }
}

/// <summary>Result of parsing a SKILL.md file's frontmatter.</summary>
public sealed class FrontmatterParseResult
{
    public bool IsValid { get; init; }
    public string? Error { get; init; }

    public string? Name { get; init; }
    public string? Description { get; init; }
    public string? Body { get; init; }
    public string? License { get; init; }
    public string? Compatibility { get; init; }
    public IReadOnlyDictionary<string, string>? Metadata { get; init; }

    public static FrontmatterParseResult Invalid(string error) => new() { IsValid = false, Error = error };
}
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `cd src/agent && dotnet test OpenAgent.Tests --filter "FullyQualifiedName~SkillFrontmatterParserTests" -v minimal`
Expected: `Passed! - Failed: 0, Passed: 7, Skipped: 0`

- [ ] **Step 6: Commit**

```bash
git add src/agent/OpenAgent.Skills/SkillFrontmatterParser.cs src/agent/OpenAgent.Tests/Skills/SkillFrontmatterParserTests.cs src/agent/OpenAgent.Tests/OpenAgent.Tests.csproj
git commit -m "feat(skills): add SKILL.md frontmatter parser with lenient YAML handling"
```

---

### Task 3: Skill Discovery

**Files:**
- Create: `src/agent/OpenAgent.Skills/SkillDiscovery.cs`
- Create: `src/agent/OpenAgent.Tests/Skills/SkillDiscoveryTests.cs`

- [ ] **Step 1: Write failing tests for skill discovery**

```csharp
// src/agent/OpenAgent.Tests/Skills/SkillDiscoveryTests.cs
using OpenAgent.Skills;

namespace OpenAgent.Tests.Skills;

public class SkillDiscoveryTests : IDisposable
{
    private readonly string _tempDir;

    public SkillDiscoveryTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "openagent-test-skills-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    [Fact]
    public void Discover_finds_skills_in_subdirectories()
    {
        CreateSkill("code-review", """
            ---
            name: code-review
            description: Review code for bugs and style issues.
            ---
            # Code Review
            Instructions here.
            """);

        CreateSkill("deploy", """
            ---
            name: deploy
            description: Deploy the application to production.
            ---
            # Deploy
            Steps here.
            """);

        var skills = SkillDiscovery.Scan(_tempDir);

        Assert.Equal(2, skills.Count);
        Assert.Contains(skills, s => s.Name == "code-review");
        Assert.Contains(skills, s => s.Name == "deploy");
    }

    [Fact]
    public void Discover_skips_directories_without_skill_md()
    {
        // Directory exists but has no SKILL.md
        Directory.CreateDirectory(Path.Combine(_tempDir, "no-skill"));
        File.WriteAllText(Path.Combine(_tempDir, "no-skill", "README.md"), "Not a skill");

        CreateSkill("real-skill", """
            ---
            name: real-skill
            description: A real skill.
            ---
            Body.
            """);

        var skills = SkillDiscovery.Scan(_tempDir);

        Assert.Single(skills);
        Assert.Equal("real-skill", skills[0].Name);
    }

    [Fact]
    public void Discover_skips_skills_with_invalid_frontmatter()
    {
        // Missing description
        CreateSkill("bad-skill", """
            ---
            name: bad-skill
            ---
            Body.
            """);

        CreateSkill("good-skill", """
            ---
            name: good-skill
            description: A good skill.
            ---
            Body.
            """);

        var skills = SkillDiscovery.Scan(_tempDir);

        Assert.Single(skills);
        Assert.Equal("good-skill", skills[0].Name);
    }

    [Fact]
    public void Discover_returns_empty_list_when_directory_missing()
    {
        var nonExistent = Path.Combine(_tempDir, "does-not-exist");

        var skills = SkillDiscovery.Scan(nonExistent);

        Assert.Empty(skills);
    }

    [Fact]
    public void Discover_sets_absolute_location_path()
    {
        CreateSkill("my-skill", """
            ---
            name: my-skill
            description: Does something.
            ---
            Body.
            """);

        var skills = SkillDiscovery.Scan(_tempDir);

        Assert.Single(skills);
        Assert.True(Path.IsPathRooted(skills[0].Location));
        Assert.True(skills[0].Location.EndsWith("SKILL.md"), $"Expected location to end with SKILL.md, got: {skills[0].Location}");
    }

    [Fact]
    public void Discover_respects_max_skills_limit()
    {
        for (var i = 0; i < 5; i++)
        {
            CreateSkill($"skill-{i}", $"""
                ---
                name: skill-{i}
                description: Skill number {i}.
                ---
                Body.
                """);
        }

        var skills = SkillDiscovery.Scan(_tempDir, maxSkills: 3);

        Assert.Equal(3, skills.Count);
    }

    private void CreateSkill(string name, string content)
    {
        var dir = Path.Combine(_tempDir, name);
        Directory.CreateDirectory(dir);
        // Dedent the content (tests use indented raw strings for readability)
        var lines = content.Split('\n').Select(l => l.TrimStart()).ToArray();
        File.WriteAllText(Path.Combine(dir, "SKILL.md"), string.Join("\n", lines));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `cd src/agent && dotnet test OpenAgent.Tests --filter "FullyQualifiedName~SkillDiscoveryTests" --no-restore`
Expected: Build failure — `SkillDiscovery` does not exist yet.

- [ ] **Step 3: Implement skill discovery**

```csharp
// src/agent/OpenAgent.Skills/SkillDiscovery.cs
using Microsoft.Extensions.Logging;

namespace OpenAgent.Skills;

/// <summary>
/// Scans a directory for skill subdirectories containing SKILL.md files.
/// Each valid skill directory must contain a SKILL.md with valid frontmatter.
/// Follows the Agent Skills spec: one level deep, skip hidden/ignored dirs.
/// </summary>
public static class SkillDiscovery
{
    private static readonly HashSet<string> IgnoredDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", "node_modules", "dist", ".venv", "__pycache__", ".cache", "build", "bin", "obj"
    };

    /// <summary>Max SKILL.md file size in bytes (256 KB per spec).</summary>
    private const int MaxSkillFileBytes = 256_000;

    /// <summary>
    /// Scans the given directory for subdirectories containing a SKILL.md file.
    /// Returns a list of successfully parsed SkillEntry objects.
    /// Silently skips invalid skills (logged at Debug level if logger provided).
    /// </summary>
    public static IReadOnlyList<SkillEntry> Scan(string skillsRoot, int maxSkills = 200, ILogger? logger = null)
    {
        if (!Directory.Exists(skillsRoot))
        {
            logger?.LogDebug("Skills directory does not exist: {Path}", skillsRoot);
            return [];
        }

        var results = new List<SkillEntry>();

        // Scan one level deep: each subdirectory is a potential skill
        var directories = Directory.GetDirectories(skillsRoot);
        var scanned = 0;

        foreach (var dir in directories)
        {
            if (results.Count >= maxSkills)
                break;

            var dirName = Path.GetFileName(dir);

            // Skip hidden and ignored directories
            if (dirName.StartsWith('.') || IgnoredDirectories.Contains(dirName))
                continue;

            var skillMdPath = Path.Combine(dir, "SKILL.md");
            if (!File.Exists(skillMdPath))
                continue;

            // Guard against oversized files
            var fileInfo = new FileInfo(skillMdPath);
            if (fileInfo.Length > MaxSkillFileBytes)
            {
                logger?.LogWarning("Skipping oversized SKILL.md ({Bytes} bytes): {Path}", fileInfo.Length, skillMdPath);
                continue;
            }

            var content = File.ReadAllText(skillMdPath);
            var parsed = SkillFrontmatterParser.Parse(content);

            if (!parsed.IsValid)
            {
                logger?.LogDebug("Skipping invalid SKILL.md in {Dir}: {Error}", dirName, parsed.Error);
                continue;
            }

            results.Add(new SkillEntry
            {
                Name = parsed.Name!,
                Description = parsed.Description!,
                Location = Path.GetFullPath(skillMdPath),
                Body = parsed.Body!,
                License = parsed.License,
                Compatibility = parsed.Compatibility,
                Metadata = parsed.Metadata
            });

            scanned++;
        }

        logger?.LogInformation("Discovered {Count} skills in {Root}", results.Count, skillsRoot);
        return results;
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `cd src/agent && dotnet test OpenAgent.Tests --filter "FullyQualifiedName~SkillDiscoveryTests" -v minimal`
Expected: `Passed! - Failed: 0, Passed: 6, Skipped: 0`

- [ ] **Step 5: Commit**

```bash
git add src/agent/OpenAgent.Skills/SkillDiscovery.cs src/agent/OpenAgent.Tests/Skills/SkillDiscoveryTests.cs
git commit -m "feat(skills): add skill discovery — scans directories for SKILL.md files"
```

---

### Task 4: Skill Catalog (Prompt Building + Lookup)

**Files:**
- Create: `src/agent/OpenAgent.Skills/SkillCatalog.cs`
- Create: `src/agent/OpenAgent.Tests/Skills/SkillCatalogTests.cs`

- [ ] **Step 1: Write failing tests for the catalog**

```csharp
// src/agent/OpenAgent.Tests/Skills/SkillCatalogTests.cs
using OpenAgent.Skills;

namespace OpenAgent.Tests.Skills;

public class SkillCatalogTests : IDisposable
{
    private readonly string _tempDir;

    public SkillCatalogTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "openagent-test-catalog-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    [Fact]
    public void BuildCatalogPrompt_returns_xml_with_skill_entries()
    {
        CreateSkill("code-review", "Review code for bugs.", "# Code Review\nInstructions.");
        CreateSkill("deploy", "Deploy to production.", "# Deploy\nSteps.");

        var catalog = new SkillCatalog(_tempDir);

        var prompt = catalog.BuildCatalogPrompt();

        Assert.Contains("<available_skills>", prompt);
        Assert.Contains("</available_skills>", prompt);
        Assert.Contains("<name>code-review</name>", prompt);
        Assert.Contains("<name>deploy</name>", prompt);
        Assert.Contains("<description>Review code for bugs.</description>", prompt);
        Assert.Contains("<description>Deploy to production.</description>", prompt);
    }

    [Fact]
    public void BuildCatalogPrompt_returns_empty_string_when_no_skills()
    {
        // Empty directory — no skills
        var catalog = new SkillCatalog(_tempDir);

        var prompt = catalog.BuildCatalogPrompt();

        Assert.Equal("", prompt);
    }

    [Fact]
    public void TryGetSkill_returns_skill_by_name()
    {
        CreateSkill("my-skill", "Does something.", "# My Skill\nBody content.");

        var catalog = new SkillCatalog(_tempDir);

        var found = catalog.TryGetSkill("my-skill", out var skill);

        Assert.True(found);
        Assert.Equal("my-skill", skill!.Name);
        Assert.Equal("# My Skill\nBody content.", skill.Body);
    }

    [Fact]
    public void TryGetSkill_returns_false_for_unknown_name()
    {
        var catalog = new SkillCatalog(_tempDir);

        var found = catalog.TryGetSkill("nonexistent", out var skill);

        Assert.False(found);
        Assert.Null(skill);
    }

    [Fact]
    public void SkillNames_returns_all_discovered_skill_names()
    {
        CreateSkill("alpha", "First.", "Body.");
        CreateSkill("beta", "Second.", "Body.");

        var catalog = new SkillCatalog(_tempDir);

        var names = catalog.SkillNames;

        Assert.Equal(2, names.Count);
        Assert.Contains("alpha", names);
        Assert.Contains("beta", names);
    }

    [Fact]
    public void Reload_picks_up_new_skills()
    {
        var catalog = new SkillCatalog(_tempDir);
        Assert.Empty(catalog.SkillNames);

        CreateSkill("new-skill", "Added later.", "Body.");
        catalog.Reload();

        Assert.Single(catalog.SkillNames);
        Assert.Contains("new-skill", catalog.SkillNames);
    }

    [Fact]
    public void BuildCatalogPrompt_includes_location()
    {
        CreateSkill("my-skill", "Does something.", "Body.");

        var catalog = new SkillCatalog(_tempDir);

        var prompt = catalog.BuildCatalogPrompt();

        Assert.Contains("<location>", prompt);
        Assert.Contains("SKILL.md</location>", prompt);
    }

    private void CreateSkill(string name, string description, string body)
    {
        var dir = Path.Combine(_tempDir, name);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "SKILL.md"), $"""
            ---
            name: {name}
            description: {description}
            ---
            {body}
            """);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `cd src/agent && dotnet test OpenAgent.Tests --filter "FullyQualifiedName~SkillCatalogTests" --no-restore`
Expected: Build failure — `SkillCatalog` does not exist yet.

- [ ] **Step 3: Implement the skill catalog**

```csharp
// src/agent/OpenAgent.Skills/SkillCatalog.cs
using System.Text;
using Microsoft.Extensions.Logging;

namespace OpenAgent.Skills;

/// <summary>
/// Holds discovered skills and provides the catalog prompt for the system message.
/// Loaded at startup, supports reload for picking up new/changed skills.
/// </summary>
public sealed class SkillCatalog
{
    private readonly string _skillsRoot;
    private readonly ILogger? _logger;
    private Dictionary<string, SkillEntry> _skills = new(StringComparer.OrdinalIgnoreCase);

    public SkillCatalog(string skillsRoot, ILogger<SkillCatalog>? logger = null)
    {
        _skillsRoot = skillsRoot;
        _logger = logger;
        LoadSkills();
    }

    /// <summary>All discovered skill names.</summary>
    public IReadOnlyList<string> SkillNames => _skills.Keys.ToList();

    /// <summary>All discovered skill entries.</summary>
    public IReadOnlyList<SkillEntry> Skills => _skills.Values.ToList();

    /// <summary>
    /// Looks up a skill by name. Returns false if not found.
    /// </summary>
    public bool TryGetSkill(string name, out SkillEntry? skill)
    {
        return _skills.TryGetValue(name, out skill);
    }

    /// <summary>
    /// Builds the XML catalog prompt for injection into the system message.
    /// Returns empty string if no skills are available (per spec: omit catalog entirely).
    /// </summary>
    public string BuildCatalogPrompt()
    {
        if (_skills.Count == 0)
            return "";

        var sb = new StringBuilder();
        sb.AppendLine("<available_skills>");

        foreach (var skill in _skills.Values)
        {
            sb.AppendLine("  <skill>");
            sb.AppendLine($"    <name>{EscapeXml(skill.Name)}</name>");
            sb.AppendLine($"    <description>{EscapeXml(skill.Description)}</description>");
            sb.AppendLine($"    <location>{EscapeXml(skill.Location)}</location>");
            sb.AppendLine("  </skill>");
        }

        sb.Append("</available_skills>");
        return sb.ToString();
    }

    /// <summary>Re-scans the skills directory and replaces the in-memory catalog.</summary>
    public void Reload()
    {
        LoadSkills();
    }

    private void LoadSkills()
    {
        var entries = SkillDiscovery.Scan(_skillsRoot, logger: _logger);
        _skills = entries.ToDictionary(e => e.Name, e => e, StringComparer.OrdinalIgnoreCase);
    }

    private static string EscapeXml(string value)
    {
        return value
            .Replace("&", "&amp;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;");
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `cd src/agent && dotnet test OpenAgent.Tests --filter "FullyQualifiedName~SkillCatalogTests" -v minimal`
Expected: `Passed! - Failed: 0, Passed: 7, Skipped: 0`

- [ ] **Step 5: Commit**

```bash
git add src/agent/OpenAgent.Skills/SkillCatalog.cs src/agent/OpenAgent.Tests/Skills/SkillCatalogTests.cs
git commit -m "feat(skills): add SkillCatalog — builds prompt XML and provides skill lookup"
```

---

### Task 5: Activate Skill Tool

**Files:**
- Create: `src/agent/OpenAgent.Skills/SkillToolHandler.cs`
- Create: `src/agent/OpenAgent.Tests/Skills/SkillToolHandlerTests.cs`

- [ ] **Step 1: Write failing tests for the activate_skill tool**

```csharp
// src/agent/OpenAgent.Tests/Skills/SkillToolHandlerTests.cs
using OpenAgent.Skills;

namespace OpenAgent.Tests.Skills;

public class SkillToolHandlerTests : IDisposable
{
    private readonly string _tempDir;

    public SkillToolHandlerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "openagent-test-tool-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    [Fact]
    public async Task ActivateSkill_returns_body_content()
    {
        CreateSkill("my-skill", "Does something.", "# My Skill\n\nDetailed instructions here.");
        var catalog = new SkillCatalog(_tempDir);
        var handler = new SkillToolHandler(catalog);
        var tool = handler.Tools[0];

        var result = await tool.ExecuteAsync("""{"name": "my-skill"}""");

        Assert.Contains("# My Skill", result);
        Assert.Contains("Detailed instructions here.", result);
        Assert.Contains("<skill_content", result);
    }

    [Fact]
    public async Task ActivateSkill_returns_error_for_unknown_skill()
    {
        var catalog = new SkillCatalog(_tempDir);
        var handler = new SkillToolHandler(catalog);
        var tool = handler.Tools[0];

        var result = await tool.ExecuteAsync("""{"name": "nonexistent"}""");

        Assert.Contains("error", result);
        Assert.Contains("not found", result);
    }

    [Fact]
    public async Task ActivateSkill_lists_bundled_resources()
    {
        CreateSkill("scripted", "Has scripts.", "# Scripted\nRun scripts/build.sh.");
        // Add a script file
        var scriptsDir = Path.Combine(_tempDir, "scripted", "scripts");
        Directory.CreateDirectory(scriptsDir);
        File.WriteAllText(Path.Combine(scriptsDir, "build.sh"), "#!/bin/bash\necho hello");

        var catalog = new SkillCatalog(_tempDir);
        var handler = new SkillToolHandler(catalog);
        var tool = handler.Tools[0];

        var result = await tool.ExecuteAsync("""{"name": "scripted"}""");

        Assert.Contains("<skill_resources>", result);
        Assert.Contains("scripts/build.sh", result);
    }

    [Fact]
    public void ToolHandler_exposes_single_tool()
    {
        var catalog = new SkillCatalog(_tempDir);
        var handler = new SkillToolHandler(catalog);

        Assert.Single(handler.Tools);
        Assert.Equal("activate_skill", handler.Tools[0].Definition.Name);
    }

    private void CreateSkill(string name, string description, string body)
    {
        var dir = Path.Combine(_tempDir, name);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "SKILL.md"), $"""
            ---
            name: {name}
            description: {description}
            ---
            {body}
            """);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `cd src/agent && dotnet test OpenAgent.Tests --filter "FullyQualifiedName~SkillToolHandlerTests" --no-restore`
Expected: Build failure — `SkillToolHandler` does not exist yet.

- [ ] **Step 3: Implement the skill tool handler**

```csharp
// src/agent/OpenAgent.Skills/SkillToolHandler.cs
using System.Text;
using System.Text.Json;
using OpenAgent.Contracts;

namespace OpenAgent.Skills;

/// <summary>
/// Tool handler that provides the activate_skill tool. When invoked, returns the
/// full SKILL.md body wrapped in structured tags per the Agent Skills client guide.
/// Also enumerates bundled resources (scripts/, references/, assets/).
/// </summary>
public sealed class SkillToolHandler : IToolHandler
{
    public IReadOnlyList<ITool> Tools { get; }

    public SkillToolHandler(SkillCatalog catalog)
    {
        Tools = [new ActivateSkillTool(catalog)];
    }
}

internal sealed class ActivateSkillTool : ITool
{
    private readonly SkillCatalog _catalog;

    public ActivateSkillTool(SkillCatalog catalog)
    {
        _catalog = catalog;
    }

    public AgentToolDefinition Definition { get; } = new()
    {
        Name = "activate_skill",
        Description = "Load full instructions for a skill. Call this when a task matches a skill's description from the available skills catalog.",
        Parameters = new
        {
            type = "object",
            properties = new
            {
                name = new
                {
                    type = "string",
                    description = "The skill name from the available_skills catalog"
                }
            },
            required = new[] { "name" }
        }
    };

    public Task<string> ExecuteAsync(string arguments, CancellationToken ct = default)
    {
        var args = JsonDocument.Parse(arguments).RootElement;
        var name = args.GetProperty("name").GetString()
            ?? throw new ArgumentException("name is required");

        if (!_catalog.TryGetSkill(name, out var skill))
            return Task.FromResult(JsonSerializer.Serialize(new { error = $"Skill '{name}' not found" }));

        // Build structured response per Agent Skills client guide
        var sb = new StringBuilder();
        sb.AppendLine($"<skill_content name=\"{skill!.Name}\">");
        sb.AppendLine(skill.Body);

        // Resolve skill directory and list bundled resources
        var skillDir = Path.GetDirectoryName(skill.Location)!;
        var resources = EnumerateResources(skillDir);

        sb.AppendLine();
        sb.AppendLine($"Skill directory: {skillDir}");
        sb.AppendLine("Relative paths in this skill are relative to the skill directory.");

        if (resources.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("<skill_resources>");
            foreach (var resource in resources)
                sb.AppendLine($"  <file>{resource}</file>");
            sb.AppendLine("</skill_resources>");
        }

        sb.Append("</skill_content>");

        return Task.FromResult(sb.ToString());
    }

    /// <summary>
    /// Lists files in scripts/, references/, and assets/ subdirectories relative to the skill root.
    /// Caps at 50 entries to avoid flooding context.
    /// </summary>
    private static List<string> EnumerateResources(string skillDir)
    {
        var resources = new List<string>();
        var resourceDirs = new[] { "scripts", "references", "assets" };
        const int maxResources = 50;

        foreach (var subDir in resourceDirs)
        {
            var fullSubDir = Path.Combine(skillDir, subDir);
            if (!Directory.Exists(fullSubDir))
                continue;

            foreach (var file in Directory.GetFiles(fullSubDir, "*", SearchOption.AllDirectories))
            {
                if (resources.Count >= maxResources)
                    break;

                // Return path relative to skill directory using forward slashes
                var relative = Path.GetRelativePath(skillDir, file).Replace('\\', '/');
                resources.Add(relative);
            }
        }

        return resources;
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `cd src/agent && dotnet test OpenAgent.Tests --filter "FullyQualifiedName~SkillToolHandlerTests" -v minimal`
Expected: `Passed! - Failed: 0, Passed: 4, Skipped: 0`

- [ ] **Step 5: Commit**

```bash
git add src/agent/OpenAgent.Skills/SkillToolHandler.cs src/agent/OpenAgent.Tests/Skills/SkillToolHandlerTests.cs
git commit -m "feat(skills): add activate_skill tool — returns skill body with structured tags"
```

---

### Task 6: Integrate into SystemPromptBuilder, Bootstrap, and DI

**Files:**
- Modify: `src/agent/OpenAgent/OpenAgent.csproj`
- Modify: `src/agent/OpenAgent/SystemPromptBuilder.cs`
- Modify: `src/agent/OpenAgent/DataDirectoryBootstrap.cs`
- Modify: `src/agent/OpenAgent/Program.cs`

- [ ] **Step 1: Add project reference**

Add to `src/agent/OpenAgent/OpenAgent.csproj` in the ProjectReference ItemGroup:

```xml
<ProjectReference Include="..\OpenAgent.Skills\OpenAgent.Skills.csproj" />
```

- [ ] **Step 2: Add `skills` to DataDirectoryBootstrap.RequiredDirectories**

Edit `src/agent/OpenAgent/DataDirectoryBootstrap.cs` — add `"skills"` to the end of the `RequiredDirectories` array:

```csharp
// Before:
private static readonly string[] RequiredDirectories =
[
    "projects",
    "repos",
    "memory",
    "config",
    "connections"
];

// After:
private static readonly string[] RequiredDirectories =
[
    "projects",
    "repos",
    "memory",
    "config",
    "connections",
    "skills"
];
```

- [ ] **Step 3: Add SkillCatalog field and constructor parameter to SystemPromptBuilder**

Edit `src/agent/OpenAgent/SystemPromptBuilder.cs` — three surgical changes:

**3a.** Add using directive at top of file:

```csharp
using OpenAgent.Skills;
```

**3b.** Add field and update constructor. Change:

```csharp
// Before:
internal sealed class SystemPromptBuilder
{
    private readonly ILogger<SystemPromptBuilder> _logger;
    private readonly string _dataPath;
    private readonly Dictionary<string, string> _files = new();
```

to:

```csharp
// After:
internal sealed class SystemPromptBuilder
{
    private readonly ILogger<SystemPromptBuilder> _logger;
    private readonly string _dataPath;
    private readonly SkillCatalog _skillCatalog;
    private readonly Dictionary<string, string> _files = new();
```

And change the constructor from:

```csharp
// Before:
public SystemPromptBuilder(AgentEnvironment environment, ILogger<SystemPromptBuilder> logger)
{
    _logger = logger;
    _dataPath = environment.DataPath;
    LoadFiles(_dataPath);
}
```

to:

```csharp
// After:
public SystemPromptBuilder(AgentEnvironment environment, SkillCatalog skillCatalog, ILogger<SystemPromptBuilder> logger)
{
    _logger = logger;
    _dataPath = environment.DataPath;
    _skillCatalog = skillCatalog;
    LoadFiles(_dataPath);
}
```

**3c.** Add `_skillCatalog.Reload()` to the `Reload()` method. Change:

```csharp
// Before:
public void Reload()
{
    _files.Clear();
    LoadFiles(_dataPath);
}
```

to:

```csharp
// After:
public void Reload()
{
    _files.Clear();
    LoadFiles(_dataPath);
    _skillCatalog.Reload();
}
```

- [ ] **Step 4: Inject skill catalog into the Build() method**

In `SystemPromptBuilder.Build()`, add the catalog section after the foreach loop, just before the return statement. Change:

```csharp
// Before:
        return string.Join("\n\n", sections);
    }
```

to:

```csharp
// After:
        // Append skill catalog when skills are available
        var catalogPrompt = _skillCatalog.BuildCatalogPrompt();
        if (catalogPrompt.Length > 0)
        {
            var skillSection = """
                The following skills provide specialized instructions for specific tasks.
                When a task matches a skill's description, call the activate_skill tool
                with the skill's name to load its full instructions.

                """ + catalogPrompt;
            sections.Add(skillSection);
        }

        return string.Join("\n\n", sections);
    }
```

- [ ] **Step 5: Wire up DI in Program.cs**

Add `using OpenAgent.Skills;` at the top of `src/agent/OpenAgent/Program.cs`.

Register SkillCatalog and SkillToolHandler via DI. Insert after `builder.Services.AddSingleton(environment);` (line 34):

```csharp
// Skill catalog — discover skills from {dataPath}/skills/
builder.Services.AddSingleton<SkillCatalog>(sp =>
    new SkillCatalog(
        Path.Combine(environment.DataPath, "skills"),
        sp.GetRequiredService<ILogger<SkillCatalog>>()));
// SkillToolHandler registered as IToolHandler — AgentLogic aggregates all IToolHandler
// registrations via IEnumerable<IToolHandler> (see AgentLogic.cs:14-18)
builder.Services.AddSingleton<IToolHandler>(sp =>
    new SkillToolHandler(sp.GetRequiredService<SkillCatalog>()));
```

- [ ] **Step 6: Verify the full build passes**

Run: `cd src/agent && dotnet build`
Expected: `Build succeeded. 0 Warning(s) 0 Error(s)`

- [ ] **Step 7: Run all tests to verify nothing is broken**

Run: `cd src/agent && dotnet test`
Expected: All existing tests pass. No regressions.

- [ ] **Step 8: Commit**

```bash
git add src/agent/OpenAgent/SystemPromptBuilder.cs src/agent/OpenAgent/DataDirectoryBootstrap.cs src/agent/OpenAgent/Program.cs src/agent/OpenAgent/OpenAgent.csproj
git commit -m "feat(skills): integrate skill catalog into system prompt and DI wiring"
```

---

### Task 7: End-to-End Smoke Test

**Files:**
- Create: `src/agent/OpenAgent.Tests/Skills/SkillIntegrationTests.cs`

This test verifies the full pipeline: a skill on disk appears in the system prompt and is activatable via the tool.

- [ ] **Step 1: Write integration test**

```csharp
// src/agent/OpenAgent.Tests/Skills/SkillIntegrationTests.cs
using OpenAgent.Skills;

namespace OpenAgent.Tests.Skills;

/// <summary>
/// End-to-end tests verifying the full skill pipeline:
/// discovery -> catalog prompt -> tool activation.
/// </summary>
public class SkillIntegrationTests : IDisposable
{
    private readonly string _tempDir;

    public SkillIntegrationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "openagent-test-e2e-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    [Fact]
    public async Task Full_pipeline_discover_catalog_activate()
    {
        // Arrange — create a skill on disk
        var skillDir = Path.Combine(_tempDir, "git-workflow");
        Directory.CreateDirectory(skillDir);
        File.WriteAllText(Path.Combine(skillDir, "SKILL.md"), """
            ---
            name: git-workflow
            description: Manage git branches, commits, and pull requests.
            ---
            # Git Workflow

            ## Creating a feature branch
            1. Run `git checkout -b feature/name`
            2. Make your changes
            3. Commit with `git commit -m "feat: description"`
            """);

        // Act — build catalog and activate
        var catalog = new SkillCatalog(_tempDir);
        var handler = new SkillToolHandler(catalog);
        var tool = handler.Tools[0];

        // Verify catalog prompt
        var prompt = catalog.BuildCatalogPrompt();
        Assert.Contains("<name>git-workflow</name>", prompt);
        Assert.Contains("Manage git branches", prompt);

        // Verify activation
        var result = await tool.ExecuteAsync("""{"name": "git-workflow"}""");
        Assert.Contains("# Git Workflow", result);
        Assert.Contains("git checkout -b feature/name", result);
        Assert.Contains("<skill_content", result);
    }

    [Fact]
    public void Reload_picks_up_new_skills_in_catalog_prompt()
    {
        var catalog = new SkillCatalog(_tempDir);

        // Initially empty
        Assert.Equal("", catalog.BuildCatalogPrompt());

        // Add a skill
        var skillDir = Path.Combine(_tempDir, "new-skill");
        Directory.CreateDirectory(skillDir);
        File.WriteAllText(Path.Combine(skillDir, "SKILL.md"), """
            ---
            name: new-skill
            description: A freshly added skill.
            ---
            Body.
            """);

        // Reload and verify
        catalog.Reload();
        var prompt = catalog.BuildCatalogPrompt();
        Assert.Contains("<name>new-skill</name>", prompt);
    }
}
```

- [ ] **Step 2: Run to verify they pass**

Run: `cd src/agent && dotnet test OpenAgent.Tests --filter "FullyQualifiedName~SkillIntegrationTests" -v minimal`
Expected: `Passed! - Failed: 0, Passed: 2, Skipped: 0`

- [ ] **Step 3: Run the full test suite**

Run: `cd src/agent && dotnet test`
Expected: All tests pass.

- [ ] **Step 4: Commit**

```bash
git add src/agent/OpenAgent.Tests/Skills/SkillIntegrationTests.cs
git commit -m "test(skills): add end-to-end integration tests for skill pipeline"
```

---

### Task 8: Update CLAUDE.md

**Files:**
- Modify: `CLAUDE.md`

- [ ] **Step 1: Add skills documentation to CLAUDE.md**

Add to the `## Project Structure` section, after `OpenAgent.Tools.Shell/`:

```markdown
  OpenAgent.Skills/                        Agent Skills (agentskills.io spec) — discovery, catalog, activation
```

Add to the `## Architecture Rules` section a new subsection:

```markdown
### Agent Skills (agentskills.io specification)
Skills are markdown instruction documents (`SKILL.md`) that teach the agent specialized workflows. Implements the open [Agent Skills](https://agentskills.io/specification) format for cross-client compatibility.
- **Discovery** — `SkillDiscovery` scans `{dataPath}/skills/*/SKILL.md` at startup
- **Catalog** — `SkillCatalog` builds `<available_skills>` XML injected into the system prompt (~50-100 tokens per skill)
- **Activation** — `activate_skill` tool returns full SKILL.md body wrapped in `<skill_content>` tags with bundled resource listing
- **Progressive disclosure** — only name + description at startup; full content on activation; resources on demand
- Skills are NOT tools — they teach the agent HOW to use existing tools for specific workflows
```

Add to the `## Key Design Decisions` section:

```markdown
- Skills follow the agentskills.io open spec — YAML frontmatter (name, description required) + markdown body. Compatible with Claude Code, Cursor, VS Code Copilot, and 30+ other clients.
- Skill catalog injected into system prompt, not sent as tool definitions. The `activate_skill` tool is the only tool addition.
- SkillCatalog constructed eagerly at startup (before DI container) so SystemPromptBuilder can use it immediately.
```

- [ ] **Step 2: Commit**

```bash
git add CLAUDE.md
git commit -m "docs: add Agent Skills architecture to CLAUDE.md"
```
