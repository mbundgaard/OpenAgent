using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

Console.OutputEncoding = Encoding.UTF8;

var baseUrl = args.Length > 0 ? args[0] : "http://localhost:5264";
var mode = args.Length > 1 ? args[1].ToLowerInvariant() : "websocket";

if (mode is not "rest" and not "websocket")
{
    Console.Error.WriteLine("Mode must be 'rest' or 'websocket'.");
    return 1;
}

Console.WriteLine($"Mode: {mode}");
Console.WriteLine($"Server: {baseUrl}");
Console.WriteLine();

using var http = new HttpClient { BaseAddress = new Uri(baseUrl) };

while (true)
{
    var conversationId = await SelectConversationAsync(http);
    Console.WriteLine();

    var exit = mode == "websocket"
        ? await RunWebSocketAsync(baseUrl, conversationId)
        : await RunRestAsync(http, conversationId);

    if (exit) return 0;
    Console.WriteLine();
}

static async Task<string> SelectConversationAsync(HttpClient http)
{
    var conversations = new List<ConversationInfo>();

    try
    {
        var response = await http.GetAsync("/api/conversations");
        if (response.IsSuccessStatusCode)
        {
            conversations = await response.Content.ReadFromJsonAsync<List<ConversationInfo>>() ?? [];
            conversations.Sort((a, b) => b.CreatedAt.CompareTo(a.CreatedAt));
        }
    }
    catch
    {
        Console.WriteLine("[Warning] Could not fetch conversations from server.");
    }

    Console.WriteLine("  0) New conversation");
    for (var i = 0; i < conversations.Count; i++)
    {
        var c = conversations[i];
        Console.WriteLine($"  {i + 1}) {c.Id[..8]}... [{c.Type}] {c.CreatedAt:g}");
    }

    Console.WriteLine();
    Console.Write("Select: ");
    var input = Console.ReadLine()?.Trim();

    if (int.TryParse(input, out var choice) && choice > 0 && choice <= conversations.Count)
    {
        var selected = conversations[choice - 1];
        Console.WriteLine($"Resuming conversation {selected.Id[..8]}...");
        return selected.Id;
    }

    var newId = Guid.NewGuid().ToString();
    Console.WriteLine($"New conversation {newId[..8]}...");
    return newId;
}

static async Task<bool> RunRestAsync(HttpClient http, string conversationId)
{
    while (true)
    {
        Console.Write("> ");
        var input = Console.ReadLine();
        if (input is null or "exit" or "quit")
            return true;
        if (input is "/back")
            return false;
        if (string.IsNullOrWhiteSpace(input))
            continue;

        try
        {
            var response = await http.PostAsJsonAsync(
                $"/api/conversations/{conversationId}/messages",
                new { content = input });

            if (!response.IsSuccessStatusCode)
            {
                Console.WriteLine($"[Error {(int)response.StatusCode}] {await response.Content.ReadAsStringAsync()}");
                continue;
            }

            using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
            var content = doc.RootElement.GetProperty("content").GetString();
            Console.WriteLine(content);
            Console.WriteLine();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Error] {ex.Message}");
        }
    }
}

static async Task<bool> RunWebSocketAsync(string baseUrl, string conversationId)
{
    var wsUrl = baseUrl.Replace("http://", "ws://").Replace("https://", "wss://");
    var uri = new Uri($"{wsUrl}/ws/conversations/{conversationId}/text");

    using var ws = new ClientWebSocket();

    try
    {
        Console.WriteLine($"Connecting to {uri}...");
        await ws.ConnectAsync(uri, CancellationToken.None);
        Console.WriteLine("Connected.");
        Console.WriteLine();
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Failed to connect: {ex.Message}");
        return false;
    }

    var receiveTask = Task.Run(async () =>
    {
        var buffer = new byte[8192];
        while (ws.State == WebSocketState.Open)
        {
            try
            {
                // Accumulate frames until we have a complete message
                var offset = 0;
                WebSocketReceiveResult result;
                do
                {
                    result = await ws.ReceiveAsync(
                        new ArraySegment<byte>(buffer, offset, buffer.Length - offset),
                        CancellationToken.None);
                    offset += result.Count;
                } while (!result.EndOfMessage);

                if (result.MessageType == WebSocketMessageType.Close)
                    break;

                var json = Encoding.UTF8.GetString(buffer, 0, offset);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                var type = root.GetProperty("type").GetString();
                if (type == "delta")
                {
                    var content = root.GetProperty("content").GetString();
                    Console.Write(content);
                }
                else if (type == "done")
                {
                    Console.WriteLine();
                    Console.WriteLine();
                    Console.Write("> ");
                }
                else if (type == "message")
                {
                    var content = root.GetProperty("content").GetString();
                    Console.WriteLine(content);
                    Console.WriteLine();
                    Console.Write("> ");
                }
            }
            catch (WebSocketException)
            {
                break;
            }
        }
    });

    var back = false;
    while (ws.State == WebSocketState.Open)
    {
        Console.Write("> ");
        var input = Console.ReadLine();
        if (input is null or "exit" or "quit")
            break;
        if (input is "/back")
        {
            back = true;
            break;
        }
        if (string.IsNullOrWhiteSpace(input))
            continue;

        var payload = JsonSerializer.SerializeToUtf8Bytes(new { content = input });
        await ws.SendAsync(payload, WebSocketMessageType.Text, true, CancellationToken.None);
    }

    if (ws.State == WebSocketState.Open)
    {
        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
    }

    await receiveTask;
    return !back;
}

record ConversationInfo(string Id, string Source, string Type, DateTimeOffset CreatedAt);
