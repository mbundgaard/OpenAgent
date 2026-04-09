using System.Text.Json;
using OpenAgent.ScheduledTasks.Models;

namespace OpenAgent.ScheduledTasks.Storage;

/// <summary>
/// Persists scheduled tasks to scheduled-tasks.json. Keeps the list in memory as the authoritative
/// working copy and writes the full file on every mutation (there are few enough tasks that partial
/// updates aren't worth the complexity). Intentionally NOT thread-safe — the service's semaphore is
/// the single concurrency gate for all reads and writes. IO errors are swallowed in Load/Save:
/// this protects against transient file locks (e.g. parallel test runners) where degraded-but-running
/// beats a crash. Changes to a locked file stay in memory until the next successful save.
/// </summary>
internal sealed class ScheduledTaskStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly string _filePath;
    private List<ScheduledTask> _tasks = [];

    public ScheduledTaskStore(string filePath)
    {
        _filePath = filePath;
    }

    /// <summary>All tasks currently in memory.</summary>
    public IReadOnlyList<ScheduledTask> Tasks => _tasks;

    /// <summary>Loads tasks from disk into memory. Handles missing or locked files gracefully.</summary>
    public void Load()
    {
        if (!File.Exists(_filePath))
        {
            _tasks = [];
            return;
        }

        try
        {
            var json = File.ReadAllText(_filePath);
            var file = JsonSerializer.Deserialize<ScheduledTaskFile>(json, JsonOptions);
            _tasks = file?.Tasks ?? [];
        }
        catch (IOException)
        {
            // File locked by another process (e.g. parallel test runners) — start with empty list
            _tasks = [];
        }
    }

    /// <summary>Persists current in-memory state to disk. Handles locked files gracefully.</summary>
    public void Save()
    {
        var dir = Path.GetDirectoryName(_filePath);
        if (dir is not null)
            Directory.CreateDirectory(dir);

        var file = new ScheduledTaskFile { Tasks = _tasks };
        var json = JsonSerializer.Serialize(file, JsonOptions);

        try
        {
            File.WriteAllText(_filePath, json);
        }
        catch (IOException)
        {
            // File locked by another process — skip this write, state remains in memory
        }
    }

    /// <summary>Returns a task by ID, or null if not found.</summary>
    public ScheduledTask? Get(string taskId) =>
        _tasks.FirstOrDefault(t => t.Id == taskId);

    /// <summary>Adds a task to the in-memory list.</summary>
    public void Add(ScheduledTask task) =>
        _tasks.Add(task);

    /// <summary>Removes a task by ID. Returns true if found.</summary>
    public bool Remove(string taskId)
    {
        var task = Get(taskId);
        if (task is null) return false;
        _tasks.Remove(task);
        return true;
    }
}
