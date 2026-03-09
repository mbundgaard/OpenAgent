using OpenAgent.Tools.WebFetch;

namespace OpenAgent.Tests.WebFetch;

public class ContentExtractorTests
{
    [Fact]
    public void Extracts_title_from_html()
    {
        var html = "<html><head><title>Test Article</title></head><body><article><h1>Test Article</h1><p>Some content here.</p></article></body></html>";

        var result = ContentExtractor.Extract(html, "https://example.com/article");

        Assert.Equal("Test Article", result.Title);
    }

    [Fact]
    public void Converts_html_to_markdown()
    {
        var html = """
            <html><head><title>Article</title></head>
            <body>
                <article>
                    <h1>Main Heading</h1>
                    <p>A paragraph with <strong>bold</strong> and <a href="https://example.com">a link</a>.</p>
                    <ul><li>Item one</li><li>Item two</li></ul>
                </article>
            </body></html>
            """;

        var result = ContentExtractor.Extract(html, "https://example.com/article");

        Assert.Contains("Main Heading", result.Content);
        Assert.Contains("**bold**", result.Content);
        Assert.Contains("a link", result.Content);
        Assert.Contains("Item one", result.Content);
    }

    [Fact]
    public void Strips_nav_and_footer_noise()
    {
        var html = """
            <html><head><title>Article</title></head>
            <body>
                <nav><a href="/">Home</a><a href="/about">About</a></nav>
                <article>
                    <h1>Real Content</h1>
                    <p>This is the article body with enough text to be considered main content by the readability algorithm.</p>
                    <p>Another paragraph to ensure SmartReader picks this up as the main content area.</p>
                </article>
                <footer>Copyright 2024</footer>
            </body></html>
            """;

        var result = ContentExtractor.Extract(html, "https://example.com/article");

        Assert.Contains("Real Content", result.Content);
        Assert.DoesNotContain("Copyright 2024", result.Content);
    }

    [Fact]
    public void Truncates_content_to_max_chars()
    {
        var longParagraph = new string('x', 1000);
        var html = $"<html><head><title>Long</title></head><body><article><p>{longParagraph}</p></article></body></html>";

        var result = ContentExtractor.Extract(html, "https://example.com", maxChars: 100);

        Assert.True(result.Content.Length <= 100);
        Assert.True(result.Truncated);
    }

    [Fact]
    public void Returns_empty_content_for_empty_html()
    {
        var result = ContentExtractor.Extract("", "https://example.com");

        Assert.NotNull(result.Content);
    }

    [Fact]
    public void Returns_char_count()
    {
        var html = "<html><head><title>Test</title></head><body><article><p>Hello world</p></article></body></html>";

        var result = ContentExtractor.Extract(html, "https://example.com");

        Assert.Equal(result.Content.Length, result.CharCount);
    }
}
