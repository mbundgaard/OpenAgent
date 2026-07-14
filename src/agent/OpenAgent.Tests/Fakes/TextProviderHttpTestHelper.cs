using System.Reflection;

namespace OpenAgent.Tests.Fakes;

/// <summary>
/// Test-only reflection seam for provider-level HTTP tests. The real text providers
/// (AnthropicSubscriptionTextProvider, AzureOpenAiTextProvider, OpenAiSubscriptionTextProvider)
/// build their own internal <see cref="HttpClient"/> inside <c>Configure()</c> with no
/// constructor injection point for the handler — there is no production seam to substitute one.
/// Rather than add an injection point to production code purely for testability, this helper
/// replaces the private <c>_httpClient</c> field via reflection AFTER <c>Configure()</c> has run,
/// preserving whatever BaseAddress / default headers Configure() set up. This is the smallest
/// change that lets a test observe the exact outbound JSON a provider sends.
/// </summary>
public static class TextProviderHttpTestHelper
{
    /// <summary>
    /// Replaces the provider's private <c>_httpClient</c> field with a new <see cref="HttpClient"/>
    /// wrapping <paramref name="handler"/>, copying BaseAddress and default request headers from
    /// the client Configure() originally created so per-provider setup (e.g. Anthropic's
    /// anthropic-version header, Azure's api-key header) still applies.
    /// </summary>
    public static void InstallStubHandler(object provider, QueuedStubHttpMessageHandler handler)
    {
        var field = provider.GetType().GetField("_httpClient", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException($"{provider.GetType().Name} has no private _httpClient field.");

        var original = field.GetValue(provider) as HttpClient
            ?? throw new InvalidOperationException("Call Configure() before InstallStubHandler so the provider has already built its HttpClient.");

        var replacement = new HttpClient(handler)
        {
            BaseAddress = original.BaseAddress,
            Timeout = original.Timeout
        };
        foreach (var header in original.DefaultRequestHeaders)
            replacement.DefaultRequestHeaders.TryAddWithoutValidation(header.Key, header.Value);

        field.SetValue(provider, replacement);
        original.Dispose();
    }
}
