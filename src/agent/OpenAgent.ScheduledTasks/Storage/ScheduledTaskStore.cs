using System.Text.Json;
using OpenAgent.ScheduledTasks.Models;

namespace OpenAgent.ScheduledTasks.Storage;

/// <summary>
/// Loads and saves scheduled tasks to a JSON file. Maintains an in-memory cache.
/// All public methods are NOT thread-safe — callers must hold the service lock.
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

    /// <summary>Loads tasks from disk into memory. Creates empty file if missing.</summary>
    public void Load()
    {
        if (!File.Exists(_filePath))
        {
            _tasks = [];
            return;
        }

        var json = File.ReadAllText(_filePath);
        var file = JsonSerializer.Deserialize<ScheduledTaskFile>(json, JsonOptions);
        _tasks = file?.Tasks ?? [];
    }

    /// <summary>Persists current in-memory state to disk.</summary>
    public void Save()
    {
        var dir = Path.GetDirectoryName(_filePath);
        if (dir is not null)
            Directory.CreateDirectory(dir);

        var file = new ScheduledTaskFile { Tasks = _tasks };
        var json = JsonSerializer.Serialize(file, JsonOptions);
        File.WriteAllText(_filePath, json);
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
