using OpenAgent.Models.Common;

namespace OpenAgent.Tests;

public class ToolOutputCapTests
{
    [Fact]
    public void Apply_OutputWithinLimit_ReturnedUnchanged()
    {
        var output = "small output";

        var result = ToolOutputCap.Apply(output, maxChars: 100);

        Assert.Equal(output, result);
    }

    [Fact]
    public void Apply_OutputExactlyAtLimit_ReturnedUnchanged()
    {
        var output = new string('x', 50);

        var result = ToolOutputCap.Apply(output, maxChars: 50);

        Assert.Equal(output, result);
    }

    [Fact]
    public void Apply_OutputOverLimit_TruncatedWithNotice()
    {
        var output = new string('x', 200);

        var result = ToolOutputCap.Apply(output, maxChars: 50);

        // Keeps the first maxChars characters (head)
        Assert.StartsWith(new string('x', 50), result);
        // Data portion is capped at maxChars — only 50 of the 200 'x' chars survive
        // (the notice contains no 'x'), proving the payload was truncated, not the whole string.
        Assert.Equal(50, result.Count(c => c == 'x'));
        // Notice signals truncation and the original size so the agent knows to narrow
        Assert.Contains("truncated", result);
        Assert.Contains("200", result);
    }

    [Fact]
    public void Apply_NullOutput_ReturnedAsIs()
    {
        var result = ToolOutputCap.Apply(null!, maxChars: 50);

        Assert.Null(result);
    }
}
