using OpenAgent.MemoryDigest;

namespace OpenAgent.Tests;

public class MarkdownSectionEditorTests
{
    private const string Sample =
        "# MEMORY\n\n" +
        "Top-level preamble line.\n\n" +
        "## Martin\n\n" +
        "Runs Muneris. Prefers concise responses.\n\n" +
        "## Projects\n\n" +
        "- OpenAgent — multi-channel agent\n" +
        "- Flex — production deployment\n\n" +
        "## Open Questions\n\n" +
        "Should we ship the digest job before background?\n";

    [Fact]
    public void Add_appends_content_to_existing_section()
    {
        var ops = new[]
        {
            new DigestOperation(DigestOperationKind.Add, "Martin", Content: "Birthday: 1980-05-12.")
        };

        var result = MarkdownSectionEditor.Apply(Sample, ops);

        Assert.True(result.Results[0].Applied);
        Assert.Contains("Runs Muneris. Prefers concise responses.\n\nBirthday: 1980-05-12.", result.Markdown);
    }

    [Fact]
    public void Add_creates_section_when_missing()
    {
        var ops = new[]
        {
            new DigestOperation(DigestOperationKind.Add, "Health", Content: "Started running again.")
        };

        var result = MarkdownSectionEditor.Apply(Sample, ops);

        Assert.True(result.Results[0].Applied);
        Assert.Contains("## Health\n\nStarted running again.", result.Markdown);
    }

    [Fact]
    public void Add_section_match_is_case_insensitive()
    {
        var ops = new[]
        {
            new DigestOperation(DigestOperationKind.Add, "martin", Content: "Note.")
        };

        var result = MarkdownSectionEditor.Apply(Sample, ops);

        Assert.True(result.Results[0].Applied);
        // Only one Martin section in output — heading not duplicated
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(result.Markdown, "## [Mm]artin"));
    }

    [Fact]
    public void Add_rejects_empty_content()
    {
        var ops = new[]
        {
            new DigestOperation(DigestOperationKind.Add, "Martin", Content: "")
        };

        var result = MarkdownSectionEditor.Apply(Sample, ops);

        Assert.False(result.Results[0].Applied);
        Assert.Contains("non-empty content", result.Results[0].Reason);
    }

    [Fact]
    public void Update_replaces_exact_substring_in_section()
    {
        var ops = new[]
        {
            new DigestOperation(
                DigestOperationKind.Update, "Projects",
                Old: "Flex — production deployment",
                New: "Flex — running live at C:\\OpenAgent")
        };

        var result = MarkdownSectionEditor.Apply(Sample, ops);

        Assert.True(result.Results[0].Applied);
        Assert.Contains("Flex — running live at C:\\OpenAgent", result.Markdown);
        Assert.DoesNotContain("Flex — production deployment", result.Markdown);
    }

    [Fact]
    public void Update_reports_when_section_missing()
    {
        var ops = new[]
        {
            new DigestOperation(DigestOperationKind.Update, "DoesNotExist", Old: "x", New: "y")
        };

        var result = MarkdownSectionEditor.Apply(Sample, ops);

        Assert.False(result.Results[0].Applied);
        Assert.Contains("not found", result.Results[0].Reason);
    }

    [Fact]
    public void Update_reports_when_old_text_missing()
    {
        var ops = new[]
        {
            new DigestOperation(DigestOperationKind.Update, "Martin", Old: "Birthday", New: "X")
        };

        var result = MarkdownSectionEditor.Apply(Sample, ops);

        Assert.False(result.Results[0].Applied);
        Assert.Contains("old text not found", result.Results[0].Reason);
    }

    [Fact]
    public void Update_with_empty_new_string_clears_the_match()
    {
        var ops = new[]
        {
            new DigestOperation(DigestOperationKind.Update, "Martin", Old: "Prefers concise responses.", New: "")
        };

        var result = MarkdownSectionEditor.Apply(Sample, ops);

        Assert.True(result.Results[0].Applied);
        Assert.DoesNotContain("Prefers concise responses.", result.Markdown);
    }

    [Fact]
    public void Remove_with_no_content_drops_whole_section()
    {
        var ops = new[]
        {
            new DigestOperation(DigestOperationKind.Remove, "Open Questions")
        };

        var result = MarkdownSectionEditor.Apply(Sample, ops);

        Assert.True(result.Results[0].Applied);
        Assert.DoesNotContain("## Open Questions", result.Markdown);
        Assert.DoesNotContain("Should we ship the digest job", result.Markdown);
    }

    [Fact]
    public void Remove_with_content_strips_substring()
    {
        var ops = new[]
        {
            new DigestOperation(DigestOperationKind.Remove, "Projects", Content: "- Flex — production deployment")
        };

        var result = MarkdownSectionEditor.Apply(Sample, ops);

        Assert.True(result.Results[0].Applied);
        Assert.DoesNotContain("Flex — production deployment", result.Markdown);
        Assert.Contains("OpenAgent — multi-channel agent", result.Markdown);
    }

    [Fact]
    public void Remove_refuses_to_drop_implicit_preamble()
    {
        var ops = new[]
        {
            new DigestOperation(DigestOperationKind.Remove, "")
        };

        var result = MarkdownSectionEditor.Apply(Sample, ops);

        Assert.False(result.Results[0].Applied);
        Assert.Contains("preamble", result.Results[0].Reason);
    }

    [Fact]
    public void Preamble_before_first_heading_is_preserved()
    {
        var ops = new[]
        {
            new DigestOperation(DigestOperationKind.Add, "Martin", Content: "x")
        };

        var result = MarkdownSectionEditor.Apply(Sample, ops);

        Assert.Contains("# MEMORY", result.Markdown);
        Assert.Contains("Top-level preamble line.", result.Markdown);
    }

    [Fact]
    public void Sequential_operations_compose()
    {
        var ops = new[]
        {
            new DigestOperation(DigestOperationKind.Add, "Martin", Content: "Likes early mornings."),
            new DigestOperation(DigestOperationKind.Update, "Martin", Old: "Likes early mornings.", New: "Likes 5am starts."),
            new DigestOperation(DigestOperationKind.Remove, "Open Questions")
        };

        var result = MarkdownSectionEditor.Apply(Sample, ops);

        Assert.All(result.Results, r => Assert.True(r.Applied, r.Reason));
        Assert.Contains("Likes 5am starts.", result.Markdown);
        Assert.DoesNotContain("Likes early mornings.", result.Markdown);
        Assert.DoesNotContain("## Open Questions", result.Markdown);
    }

    [Fact]
    public void Empty_operations_returns_normalized_markdown()
    {
        var result = MarkdownSectionEditor.Apply(Sample, Array.Empty<DigestOperation>());

        Assert.Empty(result.Results);
        // Round-trips key content
        Assert.Contains("## Martin", result.Markdown);
        Assert.Contains("## Projects", result.Markdown);
        Assert.Contains("## Open Questions", result.Markdown);
        Assert.EndsWith("\n", result.Markdown);
    }

    [Fact]
    public void Empty_file_with_add_seeds_first_section()
    {
        var ops = new[]
        {
            new DigestOperation(DigestOperationKind.Add, "Notes", Content: "First entry.")
        };

        var result = MarkdownSectionEditor.Apply("", ops);

        Assert.True(result.Results[0].Applied);
        Assert.Equal("## Notes\n\nFirst entry.\n", result.Markdown);
    }

    [Fact]
    public void Subsections_stay_inside_parent_section()
    {
        const string md =
            "## Projects\n\n" +
            "### OpenAgent\n\n" +
            "Multi-channel agent.\n\n" +
            "### Flex\n\n" +
            "Production deployment.\n";

        var ops = new[]
        {
            new DigestOperation(DigestOperationKind.Add, "Projects", Content: "### Memory\n\nThree-job system.")
        };

        var result = MarkdownSectionEditor.Apply(md, ops);

        Assert.True(result.Results[0].Applied);
        // The whole subsection chain stays under Projects
        Assert.Contains("### OpenAgent", result.Markdown);
        Assert.Contains("### Memory", result.Markdown);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(result.Markdown, @"^## Projects$", System.Text.RegularExpressions.RegexOptions.Multiline));
    }

    [Fact]
    public void Crlf_input_is_normalized()
    {
        var input = Sample.Replace("\n", "\r\n");
        var ops = new[]
        {
            new DigestOperation(DigestOperationKind.Add, "Martin", Content: "Note.")
        };

        var result = MarkdownSectionEditor.Apply(input, ops);

        Assert.True(result.Results[0].Applied);
        Assert.DoesNotContain("\r", result.Markdown);
    }
}
