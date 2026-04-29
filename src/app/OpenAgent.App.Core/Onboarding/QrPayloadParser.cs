namespace OpenAgent.App.Core.Onboarding;

/// <summary>Parses agent connection URLs of the form https://host[:port]/[path]?token=... (or with #token=).</summary>
public static class QrPayloadParser
{
    /// <summary>
    /// Parses an agent connection URL of the form https://host[:port]/[path]?token=... (or with #token=...).
    /// Returns false and a non-empty <paramref name="error"/> on failure.
    /// </summary>
    public static bool TryParse(string input, out QrPayload? payload, out string error)
    {
        payload = null;
        error = "";

        if (string.IsNullOrWhiteSpace(input))
        {
            error = "Empty input";
            return false;
        }

        if (!Uri.TryCreate(input, UriKind.Absolute, out var uri))
        {
            error = "Not a valid URL";
            return false;
        }

        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
        {
            error = $"Unsupported scheme: {uri.Scheme}";
            return false;
        }

        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            error = "URL must not contain userinfo";
            return false;
        }

        var token = ExtractToken(uri);
        if (string.IsNullOrWhiteSpace(token))
        {
            error = "Missing token";
            return false;
        }

        if (token.AsSpan().IndexOfAny('\r', '\n', '\0') >= 0)
        {
            error = "Token contains invalid characters";
            return false;
        }

        // Preserve the original authority literally (Uri.Authority drops default ports like :443 for https).
        var authority = ExtractAuthority(input, uri);
        var basePart = $"{uri.Scheme}://{authority}{uri.AbsolutePath}";
        if (!basePart.EndsWith('/')) basePart += "/";

        payload = new QrPayload(basePart, token);
        return true;
    }

    private static string ExtractAuthority(string input, Uri uri)
    {
        // Find "scheme://" then read up to the first '/', '?', or '#'.
        var schemeMarker = "://";
        var start = input.IndexOf(schemeMarker, StringComparison.Ordinal);
        if (start < 0) return uri.Authority;
        start += schemeMarker.Length;

        var end = input.Length;
        for (var i = start; i < input.Length; i++)
        {
            var c = input[i];
            if (c == '/' || c == '?' || c == '#') { end = i; break; }
        }
        return input[start..end];
    }

    private static string? ExtractToken(Uri uri)
    {
        // Accept ?token=...
        if (!string.IsNullOrEmpty(uri.Query))
        {
            foreach (var pair in uri.Query.TrimStart('?').Split('&'))
            {
                var eq = pair.IndexOf('=');
                if (eq <= 0) continue;
                if (pair[..eq] != "token") continue;
                return Uri.UnescapeDataString(pair[(eq + 1)..]);
            }
        }

        // Accept #token=... (matches the agent's startup print format)
        if (!string.IsNullOrEmpty(uri.Fragment))
        {
            var frag = uri.Fragment.TrimStart('#');
            foreach (var pair in frag.Split('&'))
            {
                var eq = pair.IndexOf('=');
                if (eq <= 0) continue;
                if (pair[..eq] != "token") continue;
                return Uri.UnescapeDataString(pair[(eq + 1)..]);
            }
        }

        return null;
    }
}
