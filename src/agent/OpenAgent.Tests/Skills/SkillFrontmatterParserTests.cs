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
