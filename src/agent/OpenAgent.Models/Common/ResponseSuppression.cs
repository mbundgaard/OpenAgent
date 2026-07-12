namespace OpenAgent.Models.Common;

/// <summary>
/// Recognizes the "[]" sentinel an agent emits to mean "nothing worth saying". Callers use it
/// to discard the turn from history and skip delivery instead of sending the literal "[]".
///
/// The sentinel is matched on the last non-empty line rather than the whole response. Models
/// routinely narrate before signing off ("Nothing new since the last check.\n\n[]"), and a
/// whole-response equality check treats those runs as real replies — which is what let the
/// background agent's conversation grow unbounded, each run re-priming the next. Matching the
/// final line instead of a trailing substring keeps a genuine reply that merely ends in "[]"
/// (e.g. "declare it as int[]") deliverable.
/// </summary>
public static class ResponseSuppression
{
    /// <summary>The literal text an agent emits to signal a silent turn.</summary>
    public const string Sentinel = "[]";

    /// <summary>
    /// True when <paramref name="response"/> ends with the sentinel on its own line, meaning the
    /// turn should be discarded and nothing delivered. Null, empty, and whitespace-only responses
    /// are not suppressed — those indicate a failed turn, not a deliberately silent one.
    /// </summary>
    public static bool IsSuppressed(string? response)
    {
        if (string.IsNullOrWhiteSpace(response))
            return false;

        // Walk backwards to the last line carrying any content; trailing blank lines are noise.
        var lines = response.Split('\n');
        for (var i = lines.Length - 1; i >= 0; i--)
        {
            var line = lines[i].Trim();
            if (line.Length == 0)
                continue;

            return line == Sentinel;
        }

        return false;
    }
}
