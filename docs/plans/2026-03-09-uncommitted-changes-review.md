# Uncommitted Changes Review (2026-03-09)

## Scope
Reviewed uncommitted changes in `src/agent` only.

## Findings

1. High: SSRF protection is bypassable via DNS hostnames
- The validator blocks literal private/loopback IPs and `localhost`, but does not resolve hostnames before allowing fetches.
- Hostnames that resolve to private/link-local/loopback targets can bypass current checks.
- References:
  - `src/agent/OpenAgent.Tools.WebFetch/UrlValidator.cs:28`
  - `src/agent/OpenAgent.Tools.WebFetch/UrlValidator.cs:33`
  - `src/agent/OpenAgent.Tools.WebFetch/WebFetchTool.cs:53`

2. Medium: `web_fetch` throws on malformed arguments instead of returning structured error
- `JsonDocument.Parse(arguments)` is not guarded.
- Invalid JSON can throw and escape `ExecuteAsync` rather than returning `{ success: false, ... }`.
- Reference:
  - `src/agent/OpenAgent.Tools.WebFetch/WebFetchTool.cs:33`

3. Medium: `maxChars` accepts invalid values and can crash on negative input
- `maxChars` is not range-validated.
- Negative values can trigger `ArgumentOutOfRangeException` at substring slicing.
- References:
  - `src/agent/OpenAgent.Tools.WebFetch/WebFetchTool.cs:45`
  - `src/agent/OpenAgent.Tools.WebFetch/ContentExtractor.cs:32`

## Validation Run
- `dotnet build src/agent/OpenAgent.sln` passed.
- `dotnet test src/agent/OpenAgent.Tests/OpenAgent.Tests.csproj` passed (`51/51`).

## Testing Gaps
Current tests do not cover:
- Malformed `web_fetch` JSON arguments
- Negative `maxChars`
- DNS-based SSRF bypass scenarios
