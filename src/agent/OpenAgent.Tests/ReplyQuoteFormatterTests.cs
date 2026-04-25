using OpenAgent.Models.Common;

namespace OpenAgent.Tests;

public class ReplyQuoteFormatterTests
{
    [Fact]
    public void Format_NullQuoted_ReturnsContentUnchanged()
    {
        var result = ReplyQuoteFormatter.Format("hello", null);
        Assert.Equal("hello", result);
    }

    [Fact]
    public void Format_EmptyQuoted_ReturnsContentUnchanged()
    {
        var result = ReplyQuoteFormatter.Format("hello", "");
        Assert.Equal("hello", result);
    }

    [Fact]
    public void Format_ShortQuoted_PrependsBlockquoteAndBlankLine()
    {
        var result = ReplyQuoteFormatter.Format("got it", "Original message");
        Assert.Equal("> Original message\n\ngot it", result);
    }

    [Fact]
    public void Format_QuotedWithNewlines_CollapsesToSingleLine()
    {
        var result = ReplyQuoteFormatter.Format("ok", "line one\nline two\nline three");
        Assert.Equal("> line one line two line three\n\nok", result);
    }

    [Fact]
    public void Format_QuotedWithTabsAndMultipleSpaces_CollapsesWhitespace()
    {
        var result = ReplyQuoteFormatter.Format("ok", "a\t\tb   c");
        Assert.Equal("> a b c\n\nok", result);
    }

    [Fact]
    public void Format_QuotedLongerThan200Chars_TruncatesWithEllipsis()
    {
        var quoted = new string('a', 250);
        var result = ReplyQuoteFormatter.Format("ok", quoted);
        var expectedQuote = new string('a', 200) + "…";
        Assert.Equal($"> {expectedQuote}\n\nok", result);
    }

    [Fact]
    public void Format_QuotedExactly200Chars_NoEllipsis()
    {
        var quoted = new string('a', 200);
        var result = ReplyQuoteFormatter.Format("ok", quoted);
        Assert.Equal($"> {quoted}\n\nok", result);
    }

    [Fact]
    public void Format_NullContent_StillEmitsQuoteWithEmptyTrailer()
    {
        var result = ReplyQuoteFormatter.Format(null, "earlier");
        Assert.Equal("> earlier\n\n", result);
    }

    [Fact]
    public void Format_EmptyContent_StillEmitsQuoteWithEmptyTrailer()
    {
        var result = ReplyQuoteFormatter.Format("", "earlier");
        Assert.Equal("> earlier\n\n", result);
    }

    [Fact]
    public void Format_QuotedWithLeadingTrailingWhitespace_Trimmed()
    {
        var result = ReplyQuoteFormatter.Format("ok", "   earlier   ");
        Assert.Equal("> earlier\n\nok", result);
    }
}
