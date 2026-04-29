using OpenAgent.App.Core.Onboarding;

namespace OpenAgent.App.Tests.Onboarding;

public class QrPayloadParserTests
{
    [Theory]
    [InlineData("https://host.example/?token=abc", "https://host.example/", "abc")]
    [InlineData("http://localhost:8080/?token=xyz", "http://localhost:8080/", "xyz")]
    [InlineData("https://host.example/#token=hashed", "https://host.example/", "hashed")]
    [InlineData("https://host.example:443/sub/?token=t", "https://host.example:443/sub/", "t")]
    [InlineData("https://host.example/?token=abc&extra=y", "https://host.example/", "abc")]  // extra params are tolerated
    [InlineData("http://host.example/?token=t", "http://host.example/", "t")]                // http with default port works
    public void Parses_url_and_token(string input, string expectedBase, string expectedToken)
    {
        var ok = QrPayloadParser.TryParse(input, out var payload, out _);
        Assert.True(ok);
        Assert.Equal(expectedBase, payload!.BaseUrl);
        Assert.Equal(expectedToken, payload.Token);
    }

    [Theory]
    [InlineData("not a url")]
    [InlineData("https://host.example/")]            // no token
    [InlineData("ftp://host.example/?token=x")]      // wrong scheme
    [InlineData("")]
    [InlineData("https://host.example/?token=")]     // empty token
    [InlineData("https://user:pass@host.example/?token=t")]  // userinfo
    public void Rejects_malformed(string input)
    {
        var ok = QrPayloadParser.TryParse(input, out _, out var error);
        Assert.False(ok);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }
}
