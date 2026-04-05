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
