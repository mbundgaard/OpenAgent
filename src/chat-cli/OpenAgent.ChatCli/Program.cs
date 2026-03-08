using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Spectre.Console;

Console.OutputEncoding = Encoding.UTF8;

// API key from environment — required for authenticated endpoints
var apiKey = Environment.GetEnvironmentVariable("OPENAGENT_API_KEY");
if (string.IsNullOrWhiteSpace(apiKey))
{
    AnsiConsole.MarkupLine("[red]OPENAGENT_API_KEY environment variable is not set.[/]");
    return 1;
}

// Known servers
var servers = new Dictionary<string, string>
{
    ["localhost"] = "http://localhost:5264",
    ["openagent-test"] = "https://openagent-test.azurewebsites.net",
};

// Header
AnsiConsole.Write(new FigletText("OpenAgent").Color(Color.DodgerBlue1));
AnsiConsole.WriteLine();

// Main navigation loop
while (true)
{
    // Select server
    var serverName = AnsiConsole.Prompt(
        new SelectionPrompt<string>()
            .Title("[bold]Select server[/]")
            .HighlightStyle(Style.Parse("dodgerblue1"))
            .AddChoices(servers.Keys.Append("Exit")));

    if (serverName == "Exit") return 0;
    var baseUrl = servers[serverName];

    // Select mode
    var modeResult = SelectMode();
    if (modeResult is null) continue; // /back to server select
    var (mode, transport) = modeResult.Value;

    using var http = new HttpClient { BaseAddress = new Uri(baseUrl) };
    http.DefaultRequestHeaders.Add("X-Api-Key", apiKey);

    // Conversation loop — /back returns here to pick another conversation
    while (true)
    {
        var conversationId = await SelectConversationAsync(http);
        if (conversationId is null) break; // /back to mode select

        // Show chat header
        var label = mode == "voice" ? "voice" : transport;
        AnsiConsole.Write(new Rule($"[dodgerblue1]{serverName}[/] · [green]{label}[/]").LeftJustified());
        AnsiConsole.WriteLine();

        var nav = mode == "voice"
            ? Nav.Exit // voice not yet implemented in CLI
            : transport == "websocket"
                ? await RunWebSocketAsync(baseUrl, conversationId, apiKey)
                : await RunRestAsync(http, conversationId);

        if (nav == Nav.Exit) return 0;
        if (nav == Nav.Menu) break; // back to server select — will re-enter outer loop
    }
}

// --- Mode selection ---

static (string Mode, string Transport)? SelectMode()
{
    var mode = AnsiConsole.Prompt(
        new SelectionPrompt<string>()
            .Title("[bold]Select mode[/]")
            .HighlightStyle(Style.Parse("dodgerblue1"))
            .AddChoices("Text", "Voice", "Back"));

    if (mode == "Back") return null;

    if (mode == "Text")
    {
        var transport = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("[bold]Select transport[/]")
                .HighlightStyle(Style.Parse("dodgerblue1"))
                .AddChoices("WebSocket (streaming)", "REST", "Back"));

        if (transport == "Back") return null;
        var transportKey = transport.StartsWith("WebSocket") ? "websocket" : "rest";
        return ("text", transportKey);
    }

    return ("voice", "voice");
}

// --- Conversation selection ---

static async Task<string?> SelectConversationAsync(HttpClient http)
{
    var conversations = new List<ConversationInfo>();

    await AnsiConsole.Status()
        .Spinner(Spinner.Known.Dots)
        .StartAsync("Fetching conversations...", async _ =>
        {
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
                AnsiConsole.MarkupLine("[yellow]Could not fetch conversations from server.[/]");
            }
        });

    // Build choices
    var choices = new List<string> { "[green]+ New conversation[/]" };
    foreach (var c in conversations)
        choices.Add($"{c.Id[..8]}... [dim]{c.Type} · {c.CreatedAt:g}[/]");
    choices.Add("[dim]Back[/]");

    var selected = AnsiConsole.Prompt(
        new SelectionPrompt<string>()
            .Title("[bold]Select conversation[/]")
            .HighlightStyle(Style.Parse("dodgerblue1"))
            .AddChoices(choices));

    if (selected == "[dim]Back[/]") return null;

    if (selected.StartsWith("[green]"))
    {
        var newId = Guid.NewGuid().ToString();
        AnsiConsole.MarkupLine($"[dim]Created {newId[..8]}...[/]");
        return newId;
    }

    // Find selected conversation by matching the truncated ID prefix
    var prefix = selected[..8];
    var match = conversations.FirstOrDefault(c => c.Id.StartsWith(prefix));
    return match?.Id ?? Guid.NewGuid().ToString();
}

// --- REST chat loop ---

static async Task<Nav> RunRestAsync(HttpClient http, string conversationId)
{
    while (true)
    {
        AnsiConsole.MarkupLine("[dodgerblue1]You[/]");
        var input = AnsiConsole.Prompt(
            new TextPrompt<string>("[dodgerblue1]>[/]").AllowEmpty());

        if (input is "exit" or "quit" or "/exit") return Nav.Exit;
        if (input is "/back") return Nav.Back;
        if (input is "/menu") return Nav.Menu;
        if (string.IsNullOrWhiteSpace(input)) continue;

        try
        {
            var events = new List<JsonElement>();

            await AnsiConsole.Status()
                .Spinner(Spinner.Known.Dots)
                .SpinnerStyle(Style.Parse("green"))
                .StartAsync("Thinking...", async _ =>
                {
                    var response = await http.PostAsJsonAsync(
                        $"/api/conversations/{conversationId}/messages",
                        new { content = input });

                    if (!response.IsSuccessStatusCode)
                    {
                        AnsiConsole.MarkupLine($"[red]Error {(int)response.StatusCode}:[/] {await response.Content.ReadAsStringAsync()}");
                        return;
                    }

                    var array = await response.Content.ReadFromJsonAsync<JsonElement[]>();
                    if (array is not null) events.AddRange(array);
                });

            if (events.Count > 0)
            {
                AnsiConsole.Write(new Rule().RuleStyle(Style.Parse("dim")));
                AnsiConsole.MarkupLine("[green]Assistant[/]");

                foreach (var evt in events)
                {
                    var type = evt.GetProperty("type").GetString();
                    if (type == "text")
                    {
                        var text = evt.GetProperty("content").GetString() ?? "";
                        Console.Write(text);
                    }
                    else if (type == "tool_call")
                    {
                        var name = evt.GetProperty("name").GetString() ?? "";
                        var arguments = evt.GetProperty("arguments").GetString() ?? "";
                        AnsiConsole.MarkupLine($"[dim]> calling [yellow]{Markup.Escape(name)}[/]({Markup.Escape(TruncateArgs(arguments))})[/]");
                    }
                }

                Console.WriteLine();
                AnsiConsole.Write(new Rule().RuleStyle(Style.Parse("dim")));
            }
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] {ex.Message}");
        }
    }
}

// --- WebSocket streaming chat loop ---

static async Task<Nav> RunWebSocketAsync(string baseUrl, string conversationId, string apiKey)
{
    var wsUrl = baseUrl.Replace("http://", "ws://").Replace("https://", "wss://");
    var uri = new Uri($"{wsUrl}/ws/conversations/{conversationId}/text");

    using var ws = new ClientWebSocket();
    ws.Options.SetRequestHeader("X-Api-Key", apiKey);

    // Connect with spinner
    try
    {
        await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .StartAsync("Connecting...", async _ =>
            {
                await ws.ConnectAsync(uri, CancellationToken.None);
            });
    }
    catch (Exception ex)
    {
        AnsiConsole.MarkupLine($"[red]Failed to connect:[/] {ex.Message}");
        return Nav.Back;
    }

    // Shared state for streaming output
    var done = new TaskCompletionSource();

    // Background receive loop
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
                    var content = root.GetProperty("content").GetString() ?? "";
                    Console.Write(content);
                }
                else if (type == "tool_call")
                {
                    var name = root.GetProperty("name").GetString() ?? "";
                    var arguments = root.GetProperty("arguments").GetString() ?? "";
                    AnsiConsole.MarkupLine($"[dim]> calling [yellow]{Markup.Escape(name)}[/]({Markup.Escape(TruncateArgs(arguments))})[/]");
                }
                else if (type == "tool_result")
                {
                    // Tool results are available but not shown in the UI
                }
                else if (type == "done")
                {
                    // Print the response separator
                    Console.WriteLine();
                    AnsiConsole.Write(new Rule().RuleStyle(Style.Parse("dim")));
                    done.TrySetResult();
                }
            }
            catch (WebSocketException)
            {
                break;
            }
        }
    });

    // Input loop
    var nav = Nav.Exit;
    while (ws.State == WebSocketState.Open)
    {
        AnsiConsole.MarkupLine("[dodgerblue1]You[/]");
        var input = AnsiConsole.Prompt(
            new TextPrompt<string>("[dodgerblue1]>[/]").AllowEmpty());

        if (input is "exit" or "quit" or "/exit") { nav = Nav.Exit; break; }
        if (input is "/back") { nav = Nav.Back; break; }
        if (input is "/menu") { nav = Nav.Menu; break; }
        if (string.IsNullOrWhiteSpace(input)) continue;

        // Send message
        var payload = JsonSerializer.SerializeToUtf8Bytes(new { content = input });
        await ws.SendAsync(payload, WebSocketMessageType.Text, true, CancellationToken.None);

        // Show assistant label and wait for streaming response
        AnsiConsole.Write(new Rule().RuleStyle(Style.Parse("dim")));
        AnsiConsole.MarkupLine("[green]Assistant[/]");
        done = new TaskCompletionSource();

        // Wait for the response to complete before prompting again
        await done.Task;
    }

    if (ws.State == WebSocketState.Open)
        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);

    await receiveTask;
    return nav;
}

// --- Helpers ---

static string TruncateArgs(string args, int max = 80)
{
    if (args.Length <= max) return args;
    return args[..max] + "...";
}

// --- Navigation signal ---

enum Nav { Back, Menu, Exit }

// --- DTOs ---

record ConversationInfo(string Id, string Source, string Type, DateTimeOffset CreatedAt);
