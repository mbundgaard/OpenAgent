using OpenAgent.Contracts;

namespace OpenAgent.Tools.WebFetch;

/// <summary>
/// Groups web fetch tools under a single handler.
/// </summary>
public sealed class WebFetchToolHandler : IToolHandler
{
    public IReadOnlyList<ITool> Tools { get; }

    public WebFetchToolHandler()
    {
        var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        var dnsResolver = new SystemDnsResolver();
        Tools = [new WebFetchTool(httpClient, dnsResolver)];
    }
}
