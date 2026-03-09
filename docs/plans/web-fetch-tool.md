# Web Fetch Tool

HTTP fetch with readable content extraction.

## Overview

Simple HTTP GET + HTML-to-markdown extraction. No JavaScript execution. For JS-heavy or bot-protected sites, falls back to PinchTab browser automation.

## Architecture

```
Agent requests URL
        ↓
    HttpClient GET
        ↓
    ┌─────────────────┐
    │ Success (2xx)?  │
    └────────┬────────┘
             │
     ┌───────┴───────┐
     │ Yes           │ No (403, bot challenge, timeout)
     ↓               ↓
Extract content   PinchTab fallback
     │               │
     ↓               ↓
  Markdown        Markdown
```

## Tool Definition

```json
{
  "name": "web_fetch",
  "description": "Fetch a URL and extract readable content as markdown",
  "parameters": {
    "url": { "type": "string", "required": true },
    "extractMode": { "type": "string", "enum": ["markdown", "text"], "default": "markdown" },
    "maxChars": { "type": "integer", "default": 50000 },
    "useBrowser": { "type": "boolean", "default": false }
  }
}
```

## Implementation

### Primary Path: HttpClient

1. Validate URL (http/https only, no private IPs)
2. Send GET with Chrome-like User-Agent
3. Check response (2xx = success, otherwise consider fallback)
4. Extract main content using HTML-to-markdown library
5. Truncate to maxChars if needed
6. Return markdown + metadata (title, url, charCount)

### Fallback Path: PinchTab

Triggered when:
- HTTP returns 403/401/429
- Response contains bot challenge markers (Cloudflare, etc.)
- Content extraction yields empty/minimal content
- `useBrowser: true` explicitly set

1. Call PinchTab API: `POST /api/v1/scrape`
2. Wait for page load + JS execution
3. Extract markdown from rendered DOM
4. Return same format as primary path

### Libraries

| Purpose | Library | Notes |
|---------|---------|-------|
| HTTP | `HttpClient` | Built-in, handles redirects |
| HTML parsing | `AngleSharp` | Modern, standards-compliant |
| Main content | `SmartReader` | .NET port of Mozilla Readability |
| Markdown | `ReverseMarkdown` | HTML → Markdown conversion |

### Configuration

```json
{
  "webFetch": {
    "maxChars": 50000,
    "maxResponseBytes": 2000000,
    "timeoutSeconds": 30,
    "userAgent": "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36...",
    "pinchTab": {
      "enabled": true,
      "baseUrl": "http://localhost:9867",
      "fallbackOn403": true,
      "fallbackOnEmpty": true
    }
  }
}
```

### Security

- **URL validation**: http/https only
- **SSRF protection**: Block private/internal IPs (10.x, 192.168.x, 127.x, etc.)
- **Redirect limit**: Max 5 redirects, re-validate each hop
- **Response size cap**: 2MB max before truncation
- **Timeout**: 30 seconds default

### Response Format

```json
{
  "success": true,
  "url": "https://example.com/article",
  "title": "Article Title",
  "content": "# Article Title\n\nExtracted markdown content...",
  "charCount": 4523,
  "truncated": false,
  "source": "http"  // or "pinchtab"
}
```

### Error Response

```json
{
  "success": false,
  "url": "https://example.com/blocked",
  "error": "Bot challenge detected, PinchTab fallback disabled",
  "httpStatus": 403
}
```

## File Structure

```
OpenAgent.Tools.WebFetch/
├── WebFetchTool.cs           # ITool implementation
├── WebFetchToolHandler.cs    # IToolHandler grouping
├── HttpFetcher.cs            # HttpClient wrapper
├── ContentExtractor.cs       # HTML → Markdown
├── PinchTabClient.cs         # PinchTab API client
├── UrlValidator.cs           # Security checks
└── WebFetchConfig.cs         # Configuration model
```

## Dependencies

```xml
<PackageReference Include="AngleSharp" Version="1.1.2" />
<PackageReference Include="SmartReader" Version="0.9.4" />
<PackageReference Include="ReverseMarkdown" Version="4.6.0" />
```

## Source

- [OpenClaw web_fetch docs](https://docs.openclaw.ai/tools/web)
- [PinchTab](https://github.com/pinchtab/pinchtab)
- [SmartReader](https://github.com/Strumenta/SmartReader)
