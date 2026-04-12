using OpenAgent.Channel.Telnyx;
using Xunit;

namespace OpenAgent.Tests;

public class TeXmlBuilderTests
{
    [Fact]
    public void GreetAndGather_produces_say_plus_gather()
    {
        var xml = TeXmlBuilder.GreetAndGather(
            greeting: "Hi, it's OpenAgent. How can I help?",
            gatherActionUrl: "https://example.com/api/webhook/telnyx/abc/speech",
            language: "en-US");

        Assert.Contains("<?xml version=\"1.0\" encoding=\"UTF-8\"?>", xml);
        Assert.Contains("<Response>", xml);
        Assert.Contains("<Gather input=\"speech\" action=\"https://example.com/api/webhook/telnyx/abc/speech\" method=\"POST\" language=\"en-US\" speechTimeout=\"auto\">", xml);
        Assert.Contains("<Say>Hi, it's OpenAgent. How can I help?</Say>", xml);
        Assert.EndsWith("</Response>", xml.TrimEnd());
    }

    [Fact]
    public void RespondAndGather_includes_agent_reply_inside_gather()
    {
        var xml = TeXmlBuilder.RespondAndGather(
            reply: "The answer is 42.",
            gatherActionUrl: "https://example.com/api/webhook/telnyx/abc/speech",
            language: "en-US");

        Assert.Contains("<Gather", xml);
        Assert.Contains("<Say>The answer is 42.</Say>", xml);
    }

    [Fact]
    public void Farewell_ends_with_hangup()
    {
        var xml = TeXmlBuilder.Farewell("Goodbye.");

        Assert.Contains("<Say>Goodbye.</Say>", xml);
        Assert.Contains("<Hangup />", xml);
        Assert.DoesNotContain("<Gather", xml);
    }

    [Fact]
    public void Reject_says_and_hangs_up()
    {
        var xml = TeXmlBuilder.Reject("Not authorised.");

        Assert.Contains("<Say>Not authorised.</Say>", xml);
        Assert.Contains("<Hangup />", xml);
    }

    [Fact]
    public void Text_is_xml_escaped()
    {
        var xml = TeXmlBuilder.Farewell("Say <hi> & go.");

        Assert.Contains("<Say>Say &lt;hi&gt; &amp; go.</Say>", xml);
        Assert.DoesNotContain("<hi>", xml.Replace("&lt;hi&gt;", ""));
    }

    [Fact]
    public void Action_url_is_attribute_escaped()
    {
        var xml = TeXmlBuilder.GreetAndGather(
            greeting: "Hi",
            gatherActionUrl: "https://example.com/path?a=1&b=2",
            language: "en-US");

        Assert.Contains("action=\"https://example.com/path?a=1&amp;b=2\"", xml);
    }
}
