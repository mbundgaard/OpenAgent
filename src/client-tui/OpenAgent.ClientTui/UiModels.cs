namespace OpenAgent.ClientTui;

internal enum ChatMode
{
    Text,
    Voice
}

internal enum ChatTransport
{
    Rest,
    WebSocket
}

internal sealed record ServerProfile(string Name, string BaseUrl);

internal sealed record ConversationListEntry(string Label, string ConversationId);

internal abstract record CompletionUiEvent;

internal sealed record CompletionTextEvent(string Content) : CompletionUiEvent;

internal sealed record CompletionToolCallEvent(string Name, string Arguments) : CompletionUiEvent;

internal sealed record CompletionToolResultEvent(string Name, string Result) : CompletionUiEvent;

internal enum ConversationType
{
    Text,
    Voice,
    ScheduledTask,
    WebHook
}

internal sealed class ConversationListItemResponse
{
    public required string Id { get; init; }
    public required string Source { get; init; }
    public required ConversationType Type { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
}

internal sealed class ApiKeyState
{
    private const string KeyName = "OPENAGENT_API_KEY";
    private readonly string _envFilePath;

    public ApiKeyState()
    {
        _envFilePath = ResolveEnvFilePath();
        Value = LoadInitialValue();
    }

    public string? Value { get; private set; }
    public bool HasValue => !string.IsNullOrWhiteSpace(Value);

    public void Set(string? value)
    {
        Value = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        PersistToEnvFile();
    }

    private string? LoadInitialValue()
    {
        var fromFile = TryReadFromEnvFile();
        if (!string.IsNullOrWhiteSpace(fromFile))
        {
            return fromFile;
        }

        return Environment.GetEnvironmentVariable(KeyName);
    }

    private string? TryReadFromEnvFile()
    {
        if (!File.Exists(_envFilePath))
        {
            return null;
        }

        foreach (var rawLine in File.ReadAllLines(_envFilePath))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            var index = line.IndexOf('=');
            if (index <= 0)
            {
                continue;
            }

            var key = line[..index].Trim();
            if (!string.Equals(key, KeyName, StringComparison.Ordinal))
            {
                continue;
            }

            var value = line[(index + 1)..].Trim();
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }

        return null;
    }

    private void PersistToEnvFile()
    {
        List<string> lines = [];

        if (File.Exists(_envFilePath))
        {
            lines.AddRange(File.ReadAllLines(_envFilePath));
        }

        var replaced = false;
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i].Trim();
            if (!line.StartsWith($"{KeyName}=", StringComparison.Ordinal))
            {
                continue;
            }

            replaced = true;
            if (HasValue)
            {
                lines[i] = $"{KeyName}={Value}";
            }
            else
            {
                lines.RemoveAt(i);
            }

            break;
        }

        if (!replaced && HasValue)
        {
            lines.Add($"{KeyName}={Value}");
        }

        var directory = Path.GetDirectoryName(_envFilePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllLines(_envFilePath, lines);
    }

    private static string ResolveEnvFilePath()
    {
        foreach (var basePath in CandidateBasePaths())
        {
            var directory = basePath;
            while (!string.IsNullOrWhiteSpace(directory))
            {
                var candidate = Path.Combine(directory, ".env");
                if (File.Exists(candidate))
                {
                    return candidate;
                }

                directory = Path.GetDirectoryName(directory);
            }
        }

        var fallback = Path.Combine(Environment.CurrentDirectory, ".env");
        return fallback;
    }

    private static IEnumerable<string> CandidateBasePaths()
    {
        yield return Environment.CurrentDirectory;
        yield return AppContext.BaseDirectory;
    }
}
