using OpenAgent.Tools.WebFetch;

namespace OpenAgent.Tests.WebFetch;

public class WebFetchToolHandlerTests
{
    [Fact]
    public void Exposes_single_web_fetch_tool()
    {
        var handler = new WebFetchToolHandler();

        Assert.Single(handler.Tools);
        Assert.Equal("web_fetch", handler.Tools[0].Definition.Name);
    }
}
