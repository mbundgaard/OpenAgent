using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenAgent.ClientTui;

internal sealed class OpenAgentApiClient
{
    private const string ApiKeyHeaderName = "X-Api-Key";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) }
    };

    private readonly HttpClient _httpClient;
    private readonly ApiKeyState _apiKeyState;

    public OpenAgentApiClient(string baseUrl, ApiKeyState apiKeyState)
    {
        _apiKeyState = apiKeyState;
        _httpClient = new HttpClient
        {
            BaseAddress = new Uri(baseUrl)
        };
    }

    public async Task<IReadOnlyList<ConversationListItemResponse>> GetConversationsAsync(CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/conversations");
        AddApiKeyHeader(request);
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                throw new UnauthorizedAccessException("Unauthorized. Missing or invalid API key.");
            }

            return [];
        }

        var items = await response.Content.ReadFromJsonAsync<List<ConversationListItemResponse>>(JsonOptions, cancellationToken);
        return items ?? [];
    }

    public async Task<IReadOnlyList<CompletionUiEvent>> SendRestMessageAsync(string conversationId, string content, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/conversations/{conversationId}/messages")
        {
            Content = JsonContent.Create(new { content })
        };
        AddApiKeyHeader(request);
        var response = await _httpClient.SendAsync(request, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var message = await response.Content.ReadAsStringAsync(cancellationToken);
            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                throw new UnauthorizedAccessException("Unauthorized. Missing or invalid API key.");
            }

            throw new InvalidOperationException($"Server returned {(int)response.StatusCode}: {message}");
        }

        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        using var document = JsonDocument.Parse(json);
        var events = new List<CompletionUiEvent>();

        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            return events;
        }

        foreach (var element in document.RootElement.EnumerateArray())
        {
            var type = element.TryGetProperty("type", out var typeProp) ? typeProp.GetString() : null;
            switch (type)
            {
                case "text":
                    events.Add(new CompletionTextEvent(element.GetProperty("content").GetString() ?? string.Empty));
                    break;
                case "tool_call":
                    events.Add(new CompletionToolCallEvent(
                        element.GetProperty("name").GetString() ?? string.Empty,
                        element.GetProperty("arguments").GetString() ?? string.Empty));
                    break;
                case "tool_result":
                    events.Add(new CompletionToolResultEvent(
                        element.GetProperty("name").GetString() ?? string.Empty,
                        element.GetProperty("result").GetString() ?? string.Empty));
                    break;
            }
        }

        return events;
    }

    public async Task<ClientWebSocket> ConnectTextSocketAsync(string conversationId, CancellationToken cancellationToken)
    {
        var ws = new ClientWebSocket();

        if (_apiKeyState.HasValue)
        {
            ws.Options.SetRequestHeader(ApiKeyHeaderName, _apiKeyState.Value);
        }

        var wsUrl = _httpClient.BaseAddress!.ToString()
            .Replace("http://", "ws://", StringComparison.OrdinalIgnoreCase)
            .Replace("https://", "wss://", StringComparison.OrdinalIgnoreCase)
            .TrimEnd('/');

        var uri = new Uri($"{wsUrl}/ws/conversations/{conversationId}/text");
        await ws.ConnectAsync(uri, cancellationToken);
        return ws;
    }

    private void AddApiKeyHeader(HttpRequestMessage request)
    {
        if (!_apiKeyState.HasValue)
        {
            return;
        }

        request.Headers.TryAddWithoutValidation(ApiKeyHeaderName, _apiKeyState.Value);
    }

    public static async Task SendSocketMessageAsync(ClientWebSocket socket, string content, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(new { content });
        await socket.SendAsync(payload, WebSocketMessageType.Text, true, cancellationToken);
    }

    public static async Task<JsonElement?> ReceiveSocketEventAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = new byte[8192];
        using var ms = new MemoryStream();

        while (true)
        {
            var segment = new ArraySegment<byte>(buffer);
            var result = await socket.ReceiveAsync(segment, cancellationToken);

            if (result.MessageType == WebSocketMessageType.Close)
            {
                return null;
            }

            if (result.Count > 0)
            {
                ms.Write(buffer, 0, result.Count);
            }

            if (result.EndOfMessage)
            {
                break;
            }
        }

        if (ms.Length == 0)
        {
            return null;
        }

        ms.Position = 0;
        using var document = await JsonDocument.ParseAsync(ms, cancellationToken: cancellationToken);
        return document.RootElement.Clone();
    }
}
