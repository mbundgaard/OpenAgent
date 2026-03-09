using System.Net.WebSockets;
using System.Text;
using Terminal.Gui;

namespace OpenAgent.ClientTui;

internal sealed class ClientTuiApp
{
    private readonly List<ServerProfile> _servers =
    [
        new("localhost", "http://localhost:5264"),
        new("openagent-test", "https://openagent-test.azurewebsites.net")
    ];

    private readonly StringBuilder _transcript = new();
    private readonly object _sync = new();
    private readonly ApiKeyState _apiKeyState = new();

    private OpenAgentApiClient _apiClient = null!;
    private int _activeServerIndex;
    private ChatMode _mode = ChatMode.Text;
    private ChatTransport _transport = ChatTransport.WebSocket;
    private string _activeConversationId = Guid.NewGuid().ToString();

    private Window _window = null!;
    private ListView _conversationListView = null!;
    private TextView _chatView = null!;
    private TextField _inputField = null!;
    private Label _serverLabel = null!;
    private Label _modeLabel = null!;
    private Label _transportLabel = null!;
    private Label _conversationLabel = null!;
    private Label _apiKeyLabel = null!;
    private Label _statusLabel = null!;

    private IReadOnlyList<ConversationListItemResponse> _conversations = [];
    private List<ConversationListEntry> _conversationEntries = [];

    private ClientWebSocket? _webSocket;
    private string? _webSocketConversationId;
    private bool _isBusy;

    public void Run()
    {
        _apiClient = new OpenAgentApiClient(_servers[_activeServerIndex].BaseUrl, _apiKeyState);

        Application.Init();

        _window = BuildMainWindow();
        UpdateHeaderLabels();
        var top = Application.Top;
        top.Add(_window);

        Application.MainLoop.AddIdle(() =>
        {
            _ = RefreshConversationsAsync();
            WriteSystemLine("Welcome to OpenAgent Client TUI.");
            WriteSystemLine("Ctrl+K palette, Ctrl+N new chat, Ctrl+R refresh, F2 server, F3 mode, F4 transport, F5 API key, Ctrl+Q quit.");
            return false;
        });

        Application.Run();

        _ = DisposeSocketAsync();
        Application.Shutdown();
    }

    private Window BuildMainWindow()
    {
        var window = new Window("OpenAgent Client TUI")
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill()
        };

        var sidePanel = new FrameView("Control")
        {
            X = 0,
            Y = 0,
            Width = 35,
            Height = Dim.Fill()
        };

        _serverLabel = new Label("Server: -") { X = 0, Y = 0, Width = Dim.Fill() };
        _modeLabel = new Label("Mode: -") { X = 0, Y = Pos.Bottom(_serverLabel), Width = Dim.Fill() };
        _transportLabel = new Label("Transport: -") { X = 0, Y = Pos.Bottom(_modeLabel), Width = Dim.Fill() };
        _conversationLabel = new Label("Conversation: -") { X = 0, Y = Pos.Bottom(_transportLabel), Width = Dim.Fill() };
        _apiKeyLabel = new Label("API Key: -") { X = 0, Y = Pos.Bottom(_conversationLabel), Width = Dim.Fill() };

        var conversationHeader = new Label("Conversations")
        {
            X = 0,
            Y = Pos.Bottom(_apiKeyLabel) + 1,
            Width = Dim.Fill()
        };

        _conversationListView = new ListView()
        {
            X = 0,
            Y = Pos.Bottom(conversationHeader),
            Width = Dim.Fill(),
            Height = Dim.Fill(4)
        };
        _conversationListView.OpenSelectedItem += args => OnConversationSelected(args.Item);

        var newButton = new Button("New")
        {
            X = 0,
            Y = Pos.Bottom(_conversationListView),
            Width = 10
        };
        newButton.Clicked += StartNewConversation;

        var refreshButton = new Button("Refresh")
        {
            X = Pos.Right(newButton) + 1,
            Y = Pos.Bottom(_conversationListView),
            Width = 10
        };
        refreshButton.Clicked += async () => await RefreshConversationsAsync();

        sidePanel.Add(
            _serverLabel,
            _modeLabel,
            _transportLabel,
            _conversationLabel,
            _apiKeyLabel,
            conversationHeader,
            _conversationListView,
            newButton,
            refreshButton);

        var chatPanel = new FrameView("Session")
        {
            X = Pos.Right(sidePanel),
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill()
        };

        _chatView = new TextView
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(4),
            ReadOnly = true,
            WordWrap = true
        };

        _statusLabel = new Label("Ready")
        {
            X = 0,
            Y = Pos.Bottom(_chatView),
            Width = Dim.Fill()
        };

        _inputField = new TextField(string.Empty)
        {
            X = 0,
            Y = Pos.Bottom(_statusLabel),
            Width = Dim.Fill(),
            Height = 1
        };
        _inputField.KeyPress += OnInputKeyPress;

        var helpLabel = new Label("Type message or command (/new, /clear, /refresh, /transport, /server, /apikey, /exit)")
        {
            X = 0,
            Y = Pos.Bottom(_inputField),
            Width = Dim.Fill()
        };

        chatPanel.Add(_chatView, _statusLabel, _inputField, helpLabel);

        window.Add(sidePanel, chatPanel);

        window.KeyPress += OnWindowKeyPress;

        return window;
    }

    private void OnInputKeyPress(View.KeyEventEventArgs args)
    {
        if (args.KeyEvent.Key != Key.Enter)
        {
            return;
        }

        args.Handled = true;
        _ = SendInputAsync();
    }

    private void OnWindowKeyPress(View.KeyEventEventArgs args)
    {
        switch (args.KeyEvent.Key)
        {
            case Key.CtrlMask | Key.Q:
                args.Handled = true;
                Application.RequestStop();
                break;
            case Key.CtrlMask | Key.N:
                args.Handled = true;
                StartNewConversation();
                break;
            case Key.CtrlMask | Key.R:
                args.Handled = true;
                _ = RefreshConversationsAsync();
                break;
            case Key.CtrlMask | Key.K:
                args.Handled = true;
                ShowCommandPalette();
                break;
            case Key.F2:
                args.Handled = true;
                CycleServer();
                break;
            case Key.F3:
                args.Handled = true;
                ToggleMode();
                break;
            case Key.F4:
                args.Handled = true;
                ToggleTransport();
                break;
            case Key.F5:
                args.Handled = true;
                PromptApiKey();
                break;
        }
    }

    private void ShowCommandPalette()
    {
        var selection = MessageBox.Query(
            62,
            18,
            "Command Palette",
            "Choose a control action.",
            "New",
            "Refresh",
            "Server",
            "Mode",
            "Transport",
            "API Key",
            "Clear",
            "Cancel");

        switch (selection)
        {
            case 0:
                StartNewConversation();
                break;
            case 1:
                _ = RefreshConversationsAsync();
                break;
            case 2:
                CycleServer();
                break;
            case 3:
                ToggleMode();
                break;
            case 4:
                ToggleTransport();
                break;
            case 5:
                PromptApiKey();
                break;
            case 6:
                ClearTranscript();
                break;
        }
    }

    private async Task SendInputAsync()
    {
        var input = _inputField.Text.ToString()?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(input))
        {
            return;
        }

        _inputField.Text = string.Empty;

        if (await HandleCommandAsync(input))
        {
            return;
        }

        if (_mode == ChatMode.Voice)
        {
            WriteSystemLine("Voice mode from TUI is not implemented yet. Switch to text mode with F3.");
            return;
        }

        lock (_sync)
        {
            if (_isBusy)
            {
                WriteSystemLine("Wait for the current response to finish.");
                return;
            }

            _isBusy = true;
        }

        WriteUserLine(input);

        try
        {
            SetStatus("Thinking...");

            if (_transport == ChatTransport.Rest)
            {
                await SendViaRestAsync(input);
            }
            else
            {
                await SendViaWebSocketAsync(input);
            }
        }
        catch (Exception exception)
        {
            if (exception is UnauthorizedAccessException)
            {
                WriteSystemLine("Unauthorized. Set API key with /apikey or F5.");
                return;
            }

            WriteSystemLine($"Error: {exception.Message}");
        }
        finally
        {
            lock (_sync)
            {
                _isBusy = false;
            }

            SetStatus("Ready");
        }
    }

    private async Task<bool> HandleCommandAsync(string commandText)
    {
        if (!commandText.StartsWith('/'))
        {
            return false;
        }

        var command = commandText.ToLowerInvariant();

        switch (command)
        {
            case "/exit":
            case "/quit":
                Application.RequestStop();
                return true;
            case "/new":
                StartNewConversation();
                return true;
            case "/refresh":
                await RefreshConversationsAsync();
                return true;
            case "/clear":
                ClearTranscript();
                return true;
            case "/server":
                CycleServer();
                return true;
            case "/mode":
                ToggleMode();
                return true;
            case "/transport":
                ToggleTransport();
                return true;
            case "/apikey":
                PromptApiKey();
                return true;
            default:
                WriteSystemLine($"Unknown command: {command}");
                return true;
        }
    }

    private async Task SendViaRestAsync(string input)
    {
        var events = await _apiClient.SendRestMessageAsync(_activeConversationId, input, CancellationToken.None);

        StartAssistantLine();

        foreach (var completionEvent in events)
        {
            switch (completionEvent)
            {
                case CompletionTextEvent textEvent:
                    AppendRaw(textEvent.Content);
                    break;
                case CompletionToolCallEvent toolCallEvent:
                    AppendRaw($"\n[tool-call] {toolCallEvent.Name} {TrimForDisplay(toolCallEvent.Arguments, 100)}\n");
                    break;
                case CompletionToolResultEvent toolResultEvent:
                    AppendRaw($"\n[tool-result] {toolResultEvent.Name} {TrimForDisplay(toolResultEvent.Result, 100)}\n");
                    break;
            }
        }

        AppendRaw("\n\n");
    }

    private async Task SendViaWebSocketAsync(string input)
    {
        var socket = await EnsureSocketAsync();

        StartAssistantLine();

        await OpenAgentApiClient.SendSocketMessageAsync(socket, input, CancellationToken.None);

        while (true)
        {
            var message = await OpenAgentApiClient.ReceiveSocketEventAsync(socket, CancellationToken.None);
            if (message is null)
            {
                throw new InvalidOperationException("WebSocket connection closed.");
            }

            var json = message.Value;
            var type = json.TryGetProperty("type", out var typeProp)
                ? typeProp.GetString()
                : null;

            if (type == "done")
            {
                AppendRaw("\n\n");
                return;
            }

            if (type == "delta")
            {
                AppendRaw(json.GetProperty("content").GetString() ?? string.Empty);
                continue;
            }

            if (type == "tool_call")
            {
                var toolName = json.GetProperty("name").GetString() ?? string.Empty;
                var toolArguments = json.GetProperty("arguments").GetString() ?? string.Empty;
                AppendRaw($"\n[tool-call] {toolName} {TrimForDisplay(toolArguments, 100)}\n");
                continue;
            }

            if (type == "tool_result")
            {
                var toolName = json.GetProperty("name").GetString() ?? string.Empty;
                var toolResult = json.GetProperty("result").GetString() ?? string.Empty;
                AppendRaw($"\n[tool-result] {toolName} {TrimForDisplay(toolResult, 100)}\n");
            }
        }
    }

    private async Task<ClientWebSocket> EnsureSocketAsync()
    {
        if (_webSocket is not null
            && _webSocket.State == WebSocketState.Open
            && string.Equals(_webSocketConversationId, _activeConversationId, StringComparison.Ordinal))
        {
            return _webSocket;
        }

        await DisposeSocketAsync();

        SetStatus("Connecting websocket...");
        _webSocket = await _apiClient.ConnectTextSocketAsync(_activeConversationId, CancellationToken.None);
        _webSocketConversationId = _activeConversationId;
        SetStatus("Connected");
        return _webSocket;
    }

    private async Task DisposeSocketAsync()
    {
        if (_webSocket is null)
        {
            return;
        }

        try
        {
            if (_webSocket.State == WebSocketState.Open)
            {
                await _webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "switch", CancellationToken.None);
            }
        }
        catch
        {
            // Best effort.
        }

        _webSocket.Dispose();
        _webSocket = null;
        _webSocketConversationId = null;
    }

    private async Task RefreshConversationsAsync()
    {
        try
        {
            SetStatus("Loading conversations...");
            _conversations = await _apiClient.GetConversationsAsync(CancellationToken.None);
            _conversationEntries = BuildConversationEntries(_conversations);
            _conversationListView.SetSource(_conversationEntries.Select(entry => entry.Label).ToList());
            SetStatus($"Loaded {_conversationEntries.Count} conversations");
        }
        catch (Exception exception)
        {
            if (exception is UnauthorizedAccessException)
            {
                SetStatus("Unauthorized");
                WriteSystemLine("Conversation list requires API key. Use /apikey or F5.");
                return;
            }

            WriteSystemLine($"Failed to load conversations: {exception.Message}");
        }
        finally
        {
            UpdateHeaderLabels();
        }
    }

    private static List<ConversationListEntry> BuildConversationEntries(IReadOnlyList<ConversationListItemResponse> conversations)
    {
        var ordered = conversations
            .OrderByDescending(item => item.CreatedAt)
            .Select(item => new ConversationListEntry(
                $"{item.Id[..Math.Min(8, item.Id.Length)]}  {item.Type}  {item.CreatedAt.LocalDateTime:g}",
                item.Id))
            .ToList();

        return ordered;
    }

    private void OnConversationSelected(int index)
    {
        if (index < 0 || index >= _conversationEntries.Count)
        {
            return;
        }

        _activeConversationId = _conversationEntries[index].ConversationId;
        _ = DisposeSocketAsync();
        UpdateHeaderLabels();
        WriteSystemLine($"Switched to conversation {_activeConversationId}");
    }

    private void StartNewConversation()
    {
        _activeConversationId = Guid.NewGuid().ToString();
        _ = DisposeSocketAsync();
        UpdateHeaderLabels();
        WriteSystemLine($"Started new conversation {_activeConversationId}");
    }

    private void CycleServer()
    {
        _activeServerIndex++;
        if (_activeServerIndex >= _servers.Count)
        {
            _activeServerIndex = 0;
        }

        _apiClient = new OpenAgentApiClient(_servers[_activeServerIndex].BaseUrl, _apiKeyState);
        _ = DisposeSocketAsync();
        UpdateHeaderLabels();
        WriteSystemLine($"Server switched to {_servers[_activeServerIndex].Name}");
        _ = RefreshConversationsAsync();
    }

    private void ToggleMode()
    {
        _mode = _mode == ChatMode.Text ? ChatMode.Voice : ChatMode.Text;
        UpdateHeaderLabels();
        WriteSystemLine($"Mode changed to {_mode}");
    }

    private void ToggleTransport()
    {
        _transport = _transport == ChatTransport.Rest ? ChatTransport.WebSocket : ChatTransport.Rest;
        _ = DisposeSocketAsync();
        UpdateHeaderLabels();
        WriteSystemLine($"Transport changed to {_transport}");
    }

    private void UpdateHeaderLabels()
    {
        _serverLabel.Text = $"Server: {_servers[_activeServerIndex].Name}";
        _modeLabel.Text = $"Mode: {_mode}";
        _transportLabel.Text = $"Transport: {_transport}";
        _conversationLabel.Text = $"Conversation: {_activeConversationId[..8]}...";
        _apiKeyLabel.Text = $"API Key: {(_apiKeyState.HasValue ? "configured" : "not set")}";
        _window.Title = $"OpenAgent Client TUI - {_servers[_activeServerIndex].Name}";
    }

    private void SetStatus(string text)
    {
        Application.MainLoop?.Invoke(() => _statusLabel.Text = $"Status: {text}");
    }

    private void WriteSystemLine(string text)
    {
        AppendRaw($"[system] {text}\n\n");
    }

    private void WriteUserLine(string text)
    {
        AppendRaw($"You: {text}\n\n");
    }

    private void StartAssistantLine()
    {
        AppendRaw("Assistant: ");
    }

    private void ClearTranscript()
    {
        _transcript.Clear();
        _chatView.Text = string.Empty;
    }

    private void AppendRaw(string text)
    {
        Application.MainLoop?.Invoke(() =>
        {
            _transcript.Append(text);
            _chatView.Text = _transcript.ToString();
            _chatView.MoveEnd();
        });
    }

    private static string TrimForDisplay(string value, int max)
    {
        if (value.Length <= max)
        {
            return value;
        }

        return value[..max] + "...";
    }

    private void PromptApiKey()
    {
        var field = new TextField(_apiKeyState.Value ?? string.Empty)
        {
            X = 1,
            Y = 1,
            Width = Dim.Fill(2),
            Secret = true
        };

        var dialog = new Dialog("API Key", 70, 9);
        dialog.Add(new Label("Set key for X-Api-Key header (blank clears):")
        {
            X = 1,
            Y = 0
        });
        dialog.Add(field);

        var saveButton = new Button("Save", is_default: true);
        var clearButton = new Button("Clear");
        var cancelButton = new Button("Cancel");

        saveButton.Clicked += () =>
        {
            _apiKeyState.Set(field.Text.ToString());
            _ = DisposeSocketAsync();
            UpdateHeaderLabels();
            WriteSystemLine(_apiKeyState.HasValue ? "API key updated." : "API key cleared.");
            Application.RequestStop(dialog);
        };

        clearButton.Clicked += () =>
        {
            _apiKeyState.Set(null);
            _ = DisposeSocketAsync();
            UpdateHeaderLabels();
            WriteSystemLine("API key cleared.");
            Application.RequestStop(dialog);
        };

        cancelButton.Clicked += () => Application.RequestStop(dialog);

        dialog.AddButton(saveButton);
        dialog.AddButton(clearButton);
        dialog.AddButton(cancelButton);

        Application.Run(dialog);
    }
}
