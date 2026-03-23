using OpenAgent.Channel.WhatsApp;

namespace OpenAgent.Tests;

public class WhatsAppMarkdownConverterTests
{
    [Fact]
    public void ToWhatsApp_Bold_ConvertsSyntax()
    {
        var result = WhatsAppMarkdownConverter.ToWhatsApp("Hello **world**");
        Assert.Contains("*world*", result);
    }

    [Fact]
    public void ToWhatsApp_Italic_ConvertsSyntax()
    {
        var result = WhatsAppMarkdownConverter.ToWhatsApp("Hello *italic*");
        Assert.Contains("_italic_", result);
    }

    [Fact]
    public void ToWhatsApp_Strikethrough_ConvertsSyntax()
    {
        var result = WhatsAppMarkdownConverter.ToWhatsApp("Hello ~~strike~~");
        Assert.Contains("~strike~", result);
    }

    [Fact]
    public void ToWhatsApp_InlineCode_PassesThrough()
    {
        var result = WhatsAppMarkdownConverter.ToWhatsApp("Use `code` here");
        Assert.Contains("`code`", result);
    }

    [Fact]
    public void ToWhatsApp_CodeBlock_PassesThrough()
    {
        var result = WhatsAppMarkdownConverter.ToWhatsApp("```\nvar x = 1;\n```");
        Assert.Contains("```", result);
        Assert.Contains("var x = 1;", result);
    }

    [Fact]
    public void ToWhatsApp_Link_ConvertsToTextAndUrl()
    {
        var result = WhatsAppMarkdownConverter.ToWhatsApp("Visit [Google](https://google.com)");
        Assert.Contains("Google", result);
        Assert.Contains("https://google.com", result);
    }

    [Fact]
    public void ToWhatsApp_Heading_ConvertsToBold()
    {
        var result = WhatsAppMarkdownConverter.ToWhatsApp("# Title");
        Assert.Contains("*Title*", result);
    }

    [Fact]
    public void ChunkText_ShortText_ReturnsSingleChunk()
    {
        var chunks = WhatsAppMarkdownConverter.ChunkText("Hello world", 4096);
        Assert.Single(chunks);
        Assert.Equal("Hello world", chunks[0]);
    }

    [Fact]
    public void ChunkText_LongText_SplitsOnParagraph()
    {
        var text = new string('a', 2000) + "\n\n" + new string('b', 2000);
        var chunks = WhatsAppMarkdownConverter.ChunkText(text, 2500);
        Assert.Equal(2, chunks.Count);
    }

    [Fact]
    public void ChunkText_ExactLimit_DoesNotSplit()
    {
        var text = new string('a', 4096);
        var chunks = WhatsAppMarkdownConverter.ChunkText(text, 4096);
        Assert.Single(chunks);
    }
}
