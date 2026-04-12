using System.Net;
using System.Text;

namespace OpenAgent.Channel.Telnyx;

/// <summary>
/// Builds Telnyx TeXML response payloads. TeXML is an XML dialect similar to
/// Twilio TwiML — Telnyx reads the returned XML to drive the call.
/// </summary>
public static class TeXmlBuilder
{
    private const string XmlHeader = "<?xml version=\"1.0\" encoding=\"UTF-8\"?>";

    /// <summary>
    /// Greet the caller and gather their next speech turn.
    /// </summary>
    public static string GreetAndGather(string greeting, string gatherActionUrl, string language = "en-US") =>
        BuildSayInsideGather(greeting, gatherActionUrl, language);

    /// <summary>
    /// Reply with the agent's text then gather the caller's next turn.
    /// Mechanically identical to GreetAndGather today; kept as a named method
    /// so call-site intent is explicit and future divergence (e.g. different
    /// voice for agent vs greeting) is a one-file change.
    /// </summary>
    public static string RespondAndGather(string reply, string gatherActionUrl, string language = "en-US") =>
        BuildSayInsideGather(reply, gatherActionUrl, language);

    /// <summary>
    /// Speak a final line then hang up.
    /// </summary>
    public static string Farewell(string line)
    {
        var sb = new StringBuilder();
        sb.AppendLine(XmlHeader);
        sb.AppendLine("<Response>");
        sb.Append("  <Say>").Append(XmlEscapeText(line)).AppendLine("</Say>");
        sb.AppendLine("  <Hangup />");
        sb.Append("</Response>");
        return sb.ToString();
    }

    /// <summary>
    /// Reject the call with a reason and hang up. Used for allowlist denials.
    /// </summary>
    public static string Reject(string reason) => Farewell(reason);

    private static string BuildSayInsideGather(string line, string gatherActionUrl, string language)
    {
        var sb = new StringBuilder();
        sb.AppendLine(XmlHeader);
        sb.AppendLine("<Response>");
        sb.Append("  <Gather input=\"speech\" action=\"")
          .Append(XmlEscapeAttribute(gatherActionUrl))
          .Append("\" method=\"POST\" language=\"")
          .Append(XmlEscapeAttribute(language))
          .AppendLine("\" speechTimeout=\"auto\">");
        sb.Append("    <Say>").Append(XmlEscapeText(line)).AppendLine("</Say>");
        sb.AppendLine("  </Gather>");
        sb.Append("</Response>");
        return sb.ToString();
    }

    private static string XmlEscapeText(string text) =>
        (text ?? "")
            .Replace("&", "&amp;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;");

    private static string XmlEscapeAttribute(string text) =>
        WebUtility.HtmlEncode(text ?? "");
}
