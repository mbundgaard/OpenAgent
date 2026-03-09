using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Spectre.Console;

Console.OutputEncoding = Encoding.UTF8;

// Load .env file if present (KEY=VALUE lines, supports comments and blank lines)
var envFile = Path.Combine(AppContext.BaseDirectory, ".env");
if (File.Exists(envFile))
{
    foreach (var line in File.ReadAllLines(envFile))
    {
        var trimmed = line.Trim();
        if (trimmed.Length == 0 || trimmed.StartsWith('#')) continue;
        var sep = trimmed.IndexOf('=');
        if (sep <= 0) continue;
        var key = trimmed[..sep].Trim();
        var value = trimmed[(sep + 1)..].Trim();
        Environment.SetEnvironmentVariable(key, value);
    }
}

// Known servers — localhost uses a hardcoded dev key, remote servers use the env var
const string devApiKey = "dev-api-key-change-me";
var servers = new Dictionary<string, (string Url, string ApiKey)>
{
    ["localhost"] = ("http://localhost:5264", devApiKey),
    ["openagent-test"] = ("https://openagent-test.azurewebsites.net",
        Environment.GetEnvironmentVariable("OPENAGENT_API_KEY") ?? ""),
};

// Header
AnsiConsole.MarkupLine("""
[dodgerblue1]
   ██████╗ ██████╗ ███████╗███╗   ██╗
  ██╔═══██╗██╔══██╗██╔════╝████╗  ██║
  ██║   ██║██████╔╝█████╗  ██╔██╗ ██║
  ██║   ██║██╔═══╝ ██╔══╝  ██║╚██╗██║
  ╚██████╔╝██║     ███████╗██║ ╚████║
   ╚═════╝ ╚═╝     ╚══════╝╚═╝  ╚═══╝

   █████╗  ██████╗ ███████╗███╗   ██╗████████╗
  ██╔══██╗██╔════╝ ██╔════╝████╗  ██║╚══██╔══╝
  ███████║██║  ███╗█████╗  ██╔██╗ ██║   ██║
  ██╔══██║██║   ██║██╔══╝  ██║╚██╗██║   ██║
  ██║  ██║╚██████╔╝███████╗██║ ╚████║   ██║
  ╚═╝  ╚═╝ ╚═════╝ ╚══════╝╚═╝  ╚═══╝   ╚═╝[/]
""");
AnsiConsole.MarkupLine("[dim]  \U0001f916 something is thinking[/]");
AnsiConsole.WriteLine();
AnsiConsole.Write(new Rule().RuleStyle(Style.Parse("grey")));

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
    var (baseUrl, apiKey) = servers[serverName];

    // Validate API key for remote servers
    if (string.IsNullOrWhiteSpace(apiKey))
    {
        AnsiConsole.MarkupLine($"[red]No API key for {serverName}. Set OPENAGENT_API_KEY in .env or environment.[/]");
        continue;
    }

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
                        ToolRenderer.RenderToolCall(name, arguments);
                    }
                    else if (type == "tool_result")
                    {
                        var name = evt.GetProperty("name").GetString() ?? "";
                        var result = evt.GetProperty("result").GetString() ?? "";
                        ToolRenderer.RenderToolResult(name, result);
                    }
                }

                ToolRenderer.Flush();
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
                    ToolRenderer.RenderToolCall(name, arguments);
                }
                else if (type == "tool_result")
                {
                    var toolName = root.GetProperty("name").GetString() ?? "";
                    var toolResult = root.GetProperty("result").GetString() ?? "";
                    ToolRenderer.RenderToolResult(toolName, toolResult);
                }
                else if (type == "done")
                {
                    ToolRenderer.Flush();
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

// --- Navigation signal ---

enum Nav { Back, Menu, Exit }

// --- Tool rendering ---

enum ToolStyle
{
    Panel,            // 1: Tool calls in panels, results hidden
    PanelWithResult,  // 2: Panels for calls + truncated result panels
    Spinner,          // 3: Panel for call, spinner while waiting, then result
    Tree,             // 4: Tree view grouping consecutive tool rounds
    ColorCoded        // 5: Color-coded panels by tool handler domain
}

/// <summary>
/// Renders tool calls and results in the CLI. Switch style to change appearance.
/// </summary>
static class ToolRenderer
{
    public static ToolStyle Style { get; set; } = ToolStyle.ColorCoded;

    // State for Spinner style — tracks whether we're waiting for a result
    static CancellationTokenSource? _spinnerCts;
    static Task? _spinnerTask;

    // State for Tree style — collects tool rounds for batch rendering
    static readonly List<(string Name, string Arguments, string? Result)> _treeRounds = [];
    static string? _pendingTreeToolName;
    static string? _pendingTreeToolArgs;

    /// <summary>
    /// Renders a tool call event.
    /// </summary>
    public static void RenderToolCall(string name, string arguments)
    {
        switch (Style)
        {
            case ToolStyle.Panel:
            case ToolStyle.PanelWithResult:
                AnsiConsole.Write(BuildToolCallPanel(name, arguments));
                break;

            case ToolStyle.Spinner:
                AnsiConsole.Write(BuildToolCallPanel(name, arguments));
                StartSpinner(name);
                break;

            case ToolStyle.Tree:
                // Store pending call — will be paired with result or flushed on text/done
                FlushPendingTreeCall();
                _pendingTreeToolName = name;
                _pendingTreeToolArgs = arguments;
                break;

            case ToolStyle.ColorCoded:
                AnsiConsole.Write(BuildColorCodedPanel(name, arguments));
                break;
        }
    }

    /// <summary>
    /// Renders a tool result event.
    /// </summary>
    public static void RenderToolResult(string name, string result)
    {
        switch (Style)
        {
            case ToolStyle.PanelWithResult:
                AnsiConsole.Write(BuildToolResultPanel(name, result));
                break;

            case ToolStyle.Spinner:
                StopSpinner();
                AnsiConsole.Write(BuildToolResultPanel(name, result));
                break;

            case ToolStyle.Tree:
                // Pair result with pending call
                var callName = _pendingTreeToolName ?? name;
                var callArgs = _pendingTreeToolArgs ?? "";
                _treeRounds.Add((callName, callArgs, result));
                _pendingTreeToolName = null;
                _pendingTreeToolArgs = null;
                break;

            case ToolStyle.ColorCoded:
                AnsiConsole.Write(BuildToolResultPanel(name, result));
                break;

            // Panel style: tool results hidden
        }
    }

    /// <summary>
    /// Called when a response is complete — flushes any buffered state.
    /// </summary>
    public static void Flush()
    {
        if (Style == ToolStyle.Spinner)
            StopSpinner();

        if (Style == ToolStyle.Tree)
        {
            FlushPendingTreeCall();
            if (_treeRounds.Count > 0)
            {
                RenderTree();
                _treeRounds.Clear();
            }
        }
    }

    // --- Panel builders ---

    static Panel BuildToolCallPanel(string name, string arguments)
    {
        var body = PrettyPrintJson(arguments);
        return new Panel(Markup.Escape(body))
            .Header($"[yellow]{Markup.Escape(name)}[/]")
            .Border(BoxBorder.Rounded)
            .BorderStyle(Spectre.Console.Style.Parse("dim"));
    }

    static Panel BuildToolResultPanel(string name, string result)
    {
        var body = Truncate(PrettyPrintJson(result), maxLines: 10);
        return new Panel($"[dim]{Markup.Escape(body)}[/]")
            .Header($"[green]{Markup.Escape(name)}[/] [dim]result[/]")
            .Border(BoxBorder.Rounded)
            .BorderStyle(Spectre.Console.Style.Parse("dim"));
    }

    static Panel BuildColorCodedPanel(string name, string arguments)
    {
        var color = GetToolColor(name);
        var body = PrettyPrintJson(arguments);
        return new Panel(Markup.Escape(body))
            .Header($"[{color}]{Markup.Escape(name)}[/]")
            .Border(BoxBorder.Rounded)
            .BorderStyle(Spectre.Console.Style.Parse(color));
    }

    // --- Spinner helpers ---

    static void StartSpinner(string name)
    {
        _spinnerCts = new CancellationTokenSource();
        var ct = _spinnerCts.Token;
        _spinnerTask = Task.Run(async () =>
        {
            var frames = new[] { "|", "/", "-", "\\" };
            var i = 0;
            while (!ct.IsCancellationRequested)
            {
                Console.Write($"\r  [dim]{frames[i++ % frames.Length]} executing {name}...[/] ");
                try { await Task.Delay(100, ct); } catch (OperationCanceledException) { break; }
            }
            Console.Write("\r" + new string(' ', 60) + "\r");
        });
    }

    static void StopSpinner()
    {
        if (_spinnerCts is null) return;
        _spinnerCts.Cancel();
        _spinnerTask?.Wait();
        _spinnerCts.Dispose();
        _spinnerCts = null;
        _spinnerTask = null;
    }

    // --- Tree helpers ---

    static void FlushPendingTreeCall()
    {
        if (_pendingTreeToolName is not null)
        {
            _treeRounds.Add((_pendingTreeToolName, _pendingTreeToolArgs ?? "", null));
            _pendingTreeToolName = null;
            _pendingTreeToolArgs = null;
        }
    }

    static void RenderTree()
    {
        var tree = new Tree("[yellow]Tool calls[/]")
            .Style(Spectre.Console.Style.Parse("dim"));

        foreach (var (name, arguments, result) in _treeRounds)
        {
            var color = GetToolColor(name);
            var node = tree.AddNode($"[{color}]{Markup.Escape(name)}[/]");

            // Show arguments as child
            var args = PrettyPrintJson(arguments);
            node.AddNode($"[dim]{Markup.Escape(Truncate(args, maxLines: 5))}[/]");

            // Show result if available
            if (result is not null)
            {
                var res = Truncate(PrettyPrintJson(result), maxLines: 5);
                node.AddNode($"[green]result:[/] [dim]{Markup.Escape(res)}[/]");
            }
        }

        AnsiConsole.Write(tree);
    }

    // --- Color mapping by tool handler domain ---

    static string GetToolColor(string toolName)
    {
        // Match tool name prefix to a color
        if (toolName.StartsWith("file_", StringComparison.OrdinalIgnoreCase) ||
            toolName.StartsWith("fs_", StringComparison.OrdinalIgnoreCase))
            return "dodgerblue1";

        if (toolName.StartsWith("shell_", StringComparison.OrdinalIgnoreCase) ||
            toolName.StartsWith("exec_", StringComparison.OrdinalIgnoreCase) ||
            toolName.Equals("shell_exec", StringComparison.OrdinalIgnoreCase))
            return "red";

        if (toolName.StartsWith("memory_", StringComparison.OrdinalIgnoreCase))
            return "mediumpurple1";

        if (toolName.StartsWith("web_", StringComparison.OrdinalIgnoreCase) ||
            toolName.StartsWith("http_", StringComparison.OrdinalIgnoreCase))
            return "darkorange";

        return "yellow"; // default
    }

    // --- Shared helpers ---

    static string PrettyPrintJson(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var pretty = JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = true });

            // Strip outer { } and re-trim
            if (pretty.StartsWith('{') && pretty.EndsWith('}'))
            {
                pretty = pretty[1..^1].Trim();
            }

            return pretty;
        }
        catch
        {
            return json;
        }
    }

    static string Truncate(string text, int maxLines)
    {
        var lines = text.Split('\n');
        if (lines.Length <= maxLines) return text;
        return string.Join('\n', lines[..maxLines]) + "\n...";
    }
}

// --- DTOs ---

record ConversationInfo(string Id, string Source, string Type, DateTimeOffset CreatedAt);
