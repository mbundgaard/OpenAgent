using OpenAgent.Channel.Telegram;

namespace OpenAgent.Tests;

public class TelegramMarkdownConverterTests
{
    [Fact]
    public void PlainText_PassesThrough()
    {
        var result = TelegramMarkdownConverter.ToTelegramHtml("Hello world");
        Assert.Equal("Hello world", result.Trim());
    }

    [Fact]
    public void Bold_ConvertsToHtmlB()
    {
        var result = TelegramMarkdownConverter.ToTelegramHtml("This is **bold** text");
        Assert.Contains("<b>bold</b>", result);
    }

    [Fact]
    public void Italic_ConvertsToHtmlI()
    {
        var result = TelegramMarkdownConverter.ToTelegramHtml("This is *italic* text");
        Assert.Contains("<i>italic</i>", result);
    }

    [Fact]
    public void InlineCode_ConvertsToHtmlCode()
    {
        var result = TelegramMarkdownConverter.ToTelegramHtml("Use `dotnet build` here");
        Assert.Contains("<code>dotnet build</code>", result);
    }

    [Fact]
    public void CodeBlock_ConvertsToPreCode()
    {
        var result = TelegramMarkdownConverter.ToTelegramHtml("```csharp\nvar x = 1;\n```");
        Assert.Contains("<pre><code class=\"language-csharp\">", result);
        Assert.Contains("var x = 1;", result);
        Assert.Contains("</code></pre>", result);
    }

    [Fact]
    public void CodeBlock_NoLanguage_ConvertsToPreCode()
    {
        var result = TelegramMarkdownConverter.ToTelegramHtml("```\nvar x = 1;\n```");
        Assert.Contains("<pre><code>", result);
        Assert.Contains("var x = 1;", result);
    }

    [Fact]
    public void Link_ConvertsToAnchor()
    {
        var result = TelegramMarkdownConverter.ToTelegramHtml("[click here](https://example.com)");
        Assert.Contains("<a href=\"https://example.com\">click here</a>", result);
    }

    [Fact]
    public void Link_JavascriptScheme_StrippedToTextOnly()
    {
        var result = TelegramMarkdownConverter.ToTelegramHtml("[click](javascript:alert(1))");
        Assert.DoesNotContain("javascript:", result);
        Assert.Contains("click", result);
    }

    [Fact]
    public void Strikethrough_ConvertsToHtmlS()
    {
        var result = TelegramMarkdownConverter.ToTelegramHtml("This is ~~deleted~~ text");
        Assert.Contains("<s>deleted</s>", result);
    }

    [Fact]
    public void HtmlEntities_AreEscaped()
    {
        var result = TelegramMarkdownConverter.ToTelegramHtml("Use <div> & \"quotes\"");
        Assert.Contains("&lt;div&gt;", result);
        Assert.Contains("&amp;", result);
    }

    [Fact]
    public void NestedFormatting_Works()
    {
        var result = TelegramMarkdownConverter.ToTelegramHtml("This is **bold and *italic* inside**");
        Assert.Contains("<b>", result);
        Assert.Contains("<i>", result);
    }

    [Fact]
    public void ChunkMarkdown_ShortText_SingleChunk()
    {
        var chunks = TelegramMarkdownConverter.ChunkMarkdown("Hello world", 4096);
        Assert.Single(chunks);
        Assert.Equal("Hello world", chunks[0]);
    }

    [Fact]
    public void ChunkMarkdown_LongText_SplitsAtParagraphBoundary()
    {
        var paragraph1 = new string('A', 2000);
        var paragraph2 = new string('B', 2000);
        var text = $"{paragraph1}\n\n{paragraph2}";

        var chunks = TelegramMarkdownConverter.ChunkMarkdown(text, 2500);
        Assert.Equal(2, chunks.Count);
        Assert.Equal(paragraph1, chunks[0]);
        Assert.Equal(paragraph2, chunks[1]);
    }

    [Fact]
    public void ChunkMarkdown_NoParagraphBreak_SplitsAtNewline()
    {
        var line1 = new string('A', 2000);
        var line2 = new string('B', 2000);
        var text = $"{line1}\n{line2}";

        var chunks = TelegramMarkdownConverter.ChunkMarkdown(text, 2500);
        Assert.Equal(2, chunks.Count);
    }
}
