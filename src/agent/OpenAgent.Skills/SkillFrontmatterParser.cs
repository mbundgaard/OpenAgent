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
