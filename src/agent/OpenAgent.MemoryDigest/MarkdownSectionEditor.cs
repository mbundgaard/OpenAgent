using System.Text;

namespace OpenAgent.MemoryDigest;

/// <summary>
/// The kind of edit a digest operation performs on a single <c>## section</c> of MEMORY.md.
/// </summary>
public enum DigestOperationKind
{
    Add,
    Update,
    Remove
}

/// <summary>
/// A single edit produced by the digest LLM. <see cref="Section"/> selects the <c>## heading</c>
/// to operate on (case-insensitive, trimmed). <see cref="Content"/> is used by <see cref="DigestOperationKind.Add"/>
/// and <see cref="DigestOperationKind.Remove"/>. <see cref="Old"/>/<see cref="New"/> are used by
/// <see cref="DigestOperationKind.Update"/>.
/// </summary>
public sealed record DigestOperation(
    DigestOperationKind Action,
    string Section,
    string? Content = null,
    string? Old = null,
    string? New = null);

/// <summary>Outcome of applying a single operation. <see cref="Reason"/> is set when <see cref="Applied"/> is false.</summary>
public sealed record DigestOperationResult(DigestOperation Operation, bool Applied, string? Reason);

/// <summary>Result of <see cref="MarkdownSectionEditor.Apply"/> — the edited markdown plus per-op outcomes.</summary>
public sealed record DigestApplyResult(string Markdown, IReadOnlyList<DigestOperationResult> Results);

/// <summary>
/// Pure-function editor for the section-keyed JSON ops produced by the digest job. Parses MEMORY.md
/// into <c>## heading</c> blocks, applies add/update/remove operations, and re-serializes.
/// Lines before the first heading are preserved as an implicit empty-heading section so the
/// preamble survives round-trips. Section matching is case-insensitive on trimmed heading text.
/// </summary>
public static class MarkdownSectionEditor
{
    /// <summary>
    /// Apply the supplied operations to <paramref name="markdown"/>. Operations are applied in
    /// order; later ops see earlier mutations. Failures are reported in the result but never
    /// throw — the caller decides whether a partial application is acceptable.
    /// </summary>
    public static DigestApplyResult Apply(string markdown, IReadOnlyList<DigestOperation> operations)
    {
        var sections = Parse(markdown);
        var results = new List<DigestOperationResult>(operations.Count);

        foreach (var op in operations)
        {
            var result = ApplyOne(sections, op);
            results.Add(result);
        }

        return new DigestApplyResult(Serialize(sections), results);
    }

    private static DigestOperationResult ApplyOne(List<Section> sections, DigestOperation op)
    {
        var heading = (op.Section ?? string.Empty).Trim();

        switch (op.Action)
        {
            case DigestOperationKind.Add:
            {
                if (string.IsNullOrEmpty(op.Content))
                    return new DigestOperationResult(op, false, "add requires non-empty content");

                var idx = FindSection(sections, heading);
                if (idx < 0)
                {
                    sections.Add(new Section(heading, op.Content!.Trim()));
                }
                else
                {
                    var existing = sections[idx].Body;
                    var combined = existing.Length == 0
                        ? op.Content!.Trim()
                        : existing.TrimEnd() + "\n\n" + op.Content!.Trim();
                    sections[idx] = sections[idx] with { Body = combined };
                }
                return new DigestOperationResult(op, true, null);
            }

            case DigestOperationKind.Update:
            {
                if (string.IsNullOrEmpty(op.Old))
                    return new DigestOperationResult(op, false, "update requires non-empty old");
                if (op.New is null)
                    return new DigestOperationResult(op, false, "update requires new (may be empty string)");

                var idx = FindSection(sections, heading);
                if (idx < 0)
                    return new DigestOperationResult(op, false, $"section '{heading}' not found");

                var body = sections[idx].Body;
                var pos = body.IndexOf(op.Old, StringComparison.Ordinal);
                if (pos < 0)
                    return new DigestOperationResult(op, false, $"old text not found in section '{heading}'");

                var updated = body.Substring(0, pos) + op.New + body.Substring(pos + op.Old.Length);
                sections[idx] = sections[idx] with { Body = updated };
                return new DigestOperationResult(op, true, null);
            }

            case DigestOperationKind.Remove:
            {
                var idx = FindSection(sections, heading);
                if (idx < 0)
                    return new DigestOperationResult(op, false, $"section '{heading}' not found");

                if (string.IsNullOrEmpty(op.Content))
                {
                    // Whole-section removal. Refuse to delete the implicit preamble — callers should
                    // empty it via update instead, otherwise content silently disappears.
                    if (sections[idx].Heading.Length == 0)
                        return new DigestOperationResult(op, false, "cannot remove the implicit preamble section");

                    sections.RemoveAt(idx);
                    return new DigestOperationResult(op, true, null);
                }

                var body = sections[idx].Body;
                var pos = body.IndexOf(op.Content, StringComparison.Ordinal);
                if (pos < 0)
                    return new DigestOperationResult(op, false, $"content not found in section '{heading}'");

                var updated = body.Substring(0, pos) + body.Substring(pos + op.Content.Length);
                sections[idx] = sections[idx] with { Body = CollapseBlankLines(updated).Trim() };
                return new DigestOperationResult(op, true, null);
            }

            default:
                return new DigestOperationResult(op, false, $"unknown action {op.Action}");
        }
    }

    private static int FindSection(List<Section> sections, string heading)
    {
        for (var i = 0; i < sections.Count; i++)
        {
            if (string.Equals(sections[i].Heading, heading, StringComparison.OrdinalIgnoreCase))
                return i;
        }
        return -1;
    }

    private sealed record Section(string Heading, string Body);

    private static List<Section> Parse(string markdown)
    {
        var sections = new List<Section>();
        var lines = markdown.Replace("\r\n", "\n").Split('\n');

        string currentHeading = string.Empty;
        var currentBody = new StringBuilder();

        foreach (var line in lines)
        {
            // Only level-2 headings split sections. Level-3 and deeper stay inside their parent.
            if (line.StartsWith("## ") && !line.StartsWith("### "))
            {
                FlushSection(sections, currentHeading, currentBody);
                currentHeading = line.Substring(3).Trim();
                currentBody.Clear();
            }
            else
            {
                if (currentBody.Length > 0)
                    currentBody.Append('\n');
                currentBody.Append(line);
            }
        }

        FlushSection(sections, currentHeading, currentBody);
        return sections;
    }

    private static void FlushSection(List<Section> sections, string heading, StringBuilder body)
    {
        var text = body.ToString().Trim();
        // Skip the implicit preamble section if it would be empty — avoids a spurious blank
        // section when the file starts directly with "## ".
        if (heading.Length == 0 && text.Length == 0)
            return;
        sections.Add(new Section(heading, text));
    }

    private static string Serialize(List<Section> sections)
    {
        var parts = new List<string>(sections.Count);
        foreach (var section in sections)
        {
            if (section.Heading.Length == 0)
                parts.Add(section.Body);
            else if (section.Body.Length == 0)
                parts.Add($"## {section.Heading}");
            else
                parts.Add($"## {section.Heading}\n\n{section.Body}");
        }
        return string.Join("\n\n", parts).TrimEnd() + "\n";
    }

    private static string CollapseBlankLines(string text)
    {
        // After removing an inline chunk we can end up with three+ consecutive newlines; squash
        // any run of blank lines down to a single blank line so the section stays tidy.
        var sb = new StringBuilder(text.Length);
        var newlineRun = 0;
        foreach (var ch in text.Replace("\r\n", "\n"))
        {
            if (ch == '\n')
            {
                newlineRun++;
                if (newlineRun <= 2) sb.Append(ch);
            }
            else
            {
                newlineRun = 0;
                sb.Append(ch);
            }
        }
        return sb.ToString();
    }
}
