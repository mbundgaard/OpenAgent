using OpenAgent.ScheduledTasks.SystemJobs;

namespace OpenAgent.Tests;

public class SystemJobStateStoreTests : IDisposable
{
    private readonly string _statePath;

    public SystemJobStateStoreTests()
    {
        _statePath = Path.Combine(Path.GetTempPath(), "openagent-systemjobs-store-" + Guid.NewGuid().ToString("N") + ".json");
    }

    public void Dispose()
    {
        try { File.Delete(_statePath); } catch { /* best-effort */ }
    }

    [Fact]
    public void Save_then_Load_round_trips_full_state()
    {
        var writer = new SystemJobStateStore(_statePath);
        var state = writer.GetOrCreate("job-a");
        state.LastRunAt = DateTimeOffset.UtcNow.AddMinutes(-5);
        state.NextRunAt = DateTimeOffset.UtcNow.AddMinutes(10);
        state.LastStatus = "success";
        state.LastError = null;
        state.ConsecutiveErrors = 0;
        writer.Save();

        var reader = new SystemJobStateStore(_statePath);
        reader.Load();

        Assert.True(reader.All.TryGetValue("job-a", out var loaded));
        Assert.Equal(state.LastRunAt, loaded!.LastRunAt);
        Assert.Equal(state.NextRunAt, loaded.NextRunAt);
        Assert.Equal(state.LastStatus, loaded.LastStatus);
        Assert.Equal(state.ConsecutiveErrors, loaded.ConsecutiveErrors);
    }

    [Fact]
    public void Save_produces_a_file_containing_only_the_final_content_no_stray_temp_files()
    {
        var store = new SystemJobStateStore(_statePath);
        store.GetOrCreate("job-a").LastStatus = "success";
        store.Save();

        // Overwrite with a second save to exercise the "target already exists" replace path.
        store.GetOrCreate("job-b").LastStatus = "error";
        store.Save();

        Assert.True(File.Exists(_statePath));

        // The atomic-write temp file must never be left behind on a clean save - only the
        // final target file should exist in the directory for this test's file name.
        var directory = Path.GetDirectoryName(_statePath)!;
        var fileNamePrefix = Path.GetFileName(_statePath);
        var strayTempFiles = Directory.GetFiles(directory, fileNamePrefix + ".*.tmp");
        Assert.Empty(strayTempFiles);

        // The final file must be a complete, valid, fully-written round trip of both saves.
        var reader = new SystemJobStateStore(_statePath);
        reader.Load();
        Assert.Equal("success", reader.All["job-a"].LastStatus);
        Assert.Equal("error", reader.All["job-b"].LastStatus);
    }
}
