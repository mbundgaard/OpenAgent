using OpenAgent.Models.Common;

namespace OpenAgent.Tests;

public class ResponseSuppressionTests
{
    [Fact]
    public void Bare_sentinel_is_suppressed()
    {
        Assert.True(ResponseSuppression.IsSuppressed("[]"));
    }

    [Fact]
    public void Sentinel_with_surrounding_whitespace_is_suppressed()
    {
        Assert.True(ResponseSuppression.IsSuppressed("  \n [] \n\n "));
    }

    // The real-world background-agent failure: the model narrates first, then emits the
    // sentinel on its own line. Exact-match missed this, so every "silent" run was persisted
    // and the conversation grew without bound.
    [Fact]
    public void Prose_followed_by_sentinel_line_is_suppressed()
    {
        const string response = "18:21 (Sat 11 Jul) — ~52 hours post-appointment. Threads ready.\n\n[]";
        Assert.True(ResponseSuppression.IsSuppressed(response));
    }

    [Fact]
    public void Sentinel_with_trailing_whitespace_on_its_line_is_suppressed()
    {
        Assert.True(ResponseSuppression.IsSuppressed("Nothing new.\n   []   \n"));
    }

    // Guards against the naive EndsWith("[]") fix: a genuine reply may legitimately end in
    // "[]" as part of a sentence or code fragment, and must still be delivered.
    [Fact]
    public void Reply_ending_in_bracket_pair_mid_line_is_not_suppressed()
    {
        Assert.False(ResponseSuppression.IsSuppressed("Declare it as int[]"));
    }

    [Fact]
    public void Ordinary_reply_is_not_suppressed()
    {
        Assert.False(ResponseSuppression.IsSuppressed("Found something worth flagging."));
    }

    [Fact]
    public void Empty_and_null_are_not_suppressed()
    {
        Assert.False(ResponseSuppression.IsSuppressed(""));
        Assert.False(ResponseSuppression.IsSuppressed("   "));
        Assert.False(ResponseSuppression.IsSuppressed(null));
    }
}
