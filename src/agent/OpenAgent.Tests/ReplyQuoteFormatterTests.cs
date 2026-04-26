using OpenAgent.Models.Common;
using OpenAgent.Models.Conversations;

namespace OpenAgent.Tests;

public class ReplyQuoteFormatterTests
{
    // Fixed timestamp used across tests so XML output is deterministic.
    private static readonly DateTimeOffset FixedTime = new(2026, 4, 26, 6, 45, 23, TimeSpan.FromHours(2));
    private const string ExpectedTimestamp = "2026-04-26T06:45:23+02:00";

    private static Message MakeMessage(string content, string role = "assistant", DateTimeOffset? createdAt = null) => new()
    {
        Id = "m1",
        ConversationId = "c1",
        Role = role,
        Content = content,
        CreatedAt = createdAt ?? FixedTime
    };

    [Fact]
    public void Format_NullQuoted_ReturnsContentUnchanged()
    {
        var result = ReplyQuoteFormatter.Format("hello", null);
        Assert.Equal("hello", result);
    }

    [Fact]
    public void Format_EmptyQuotedContent_ReturnsContentUnchanged()
    {
        var result = ReplyQuoteFormatter.Format("hello", MakeMessage(""));
        Assert.Equal("hello", result);
    }

    [Fact]
    public void Format_ShortQuoted_EmitsXmlBlockWithAuthorAndTimestamp()
    {
        var result = ReplyQuoteFormatter.Format("got it", MakeMessage("Original message", role: "assistant"));
        Assert.Equal($"<replying_to author=\"assistant\" timestamp=\"{ExpectedTimestamp}\">\nOriginal message\n</replying_to>\n\ngot it", result);
    }

    [Fact]
    public void Format_UserAuthor_EmitsUserAttribute()
    {
        var result = ReplyQuoteFormatter.Format("ok", MakeMessage("hi", role: "user"));
        Assert.Equal($"<replying_to author=\"user\" timestamp=\"{ExpectedTimestamp}\">\nhi\n</replying_to>\n\nok", result);
    }

    [Fact]
    public void Format_QuotedWithNewlines_CollapsesToSingleLine()
    {
        var result = ReplyQuoteFormatter.Format("ok", MakeMessage("line one\nline two\nline three"));
        Assert.Equal($"<replying_to author=\"assistant\" timestamp=\"{ExpectedTimestamp}\">\nline one line two line three\n</replying_to>\n\nok", result);
    }

    [Fact]
    public void Format_QuotedWithTabsAndMultipleSpaces_CollapsesWhitespace()
    {
        var result = ReplyQuoteFormatter.Format("ok", MakeMessage("a\t\tb   c"));
        Assert.Equal($"<replying_to author=\"assistant\" timestamp=\"{ExpectedTimestamp}\">\na b c\n</replying_to>\n\nok", result);
    }

    [Fact]
    public void Format_QuotedLongerThan200Chars_TruncatesWithEllipsis()
    {
        var quoted = new string('a', 250);
        var result = ReplyQuoteFormatter.Format("ok", MakeMessage(quoted));
        var expectedQuote = new string('a', 200) + "…";
        Assert.Equal($"<replying_to author=\"assistant\" timestamp=\"{ExpectedTimestamp}\">\n{expectedQuote}\n</replying_to>\n\nok", result);
    }

    [Fact]
    public void Format_QuotedExactly200Chars_NoEllipsis()
    {
        var quoted = new string('a', 200);
        var result = ReplyQuoteFormatter.Format("ok", MakeMessage(quoted));
        Assert.Equal($"<replying_to author=\"assistant\" timestamp=\"{ExpectedTimestamp}\">\n{quoted}\n</replying_to>\n\nok", result);
    }

    [Fact]
    public void Format_NullContent_StillEmitsQuoteWithEmptyTrailer()
    {
        var result = ReplyQuoteFormatter.Format(null, MakeMessage("earlier"));
        Assert.Equal($"<replying_to author=\"assistant\" timestamp=\"{ExpectedTimestamp}\">\nearlier\n</replying_to>\n\n", result);
    }

    [Fact]
    public void Format_EmptyContent_StillEmitsQuoteWithEmptyTrailer()
    {
        var result = ReplyQuoteFormatter.Format("", MakeMessage("earlier"));
        Assert.Equal($"<replying_to author=\"assistant\" timestamp=\"{ExpectedTimestamp}\">\nearlier\n</replying_to>\n\n", result);
    }

    [Fact]
    public void Format_QuotedWithLeadingTrailingWhitespace_Trimmed()
    {
        var result = ReplyQuoteFormatter.Format("ok", MakeMessage("   earlier   "));
        Assert.Equal($"<replying_to author=\"assistant\" timestamp=\"{ExpectedTimestamp}\">\nearlier\n</replying_to>\n\nok", result);
    }

    [Fact]
    public void Format_WhitespaceOnlyQuoted_ReturnsContentUnchanged()
    {
        var result = ReplyQuoteFormatter.Format("ok", MakeMessage("   \t\n  "));
        Assert.Equal("ok", result);
    }

    [Fact]
    public void Format_DifferentTimestamp_IncludedInOutput()
    {
        var customTime = new DateTimeOffset(2026, 1, 15, 10, 30, 0, TimeSpan.Zero);
        var result = ReplyQuoteFormatter.Format("ok", MakeMessage("hi", createdAt: customTime));
        Assert.Equal("<replying_to author=\"assistant\" timestamp=\"2026-01-15T10:30:00+00:00\">\nhi\n</replying_to>\n\nok", result);
    }
}
