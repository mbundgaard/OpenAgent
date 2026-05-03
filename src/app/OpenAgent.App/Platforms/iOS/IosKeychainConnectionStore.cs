using System.Text.Json;
using Foundation;
using OpenAgent.App.Core.Services;
using Security;

namespace OpenAgent.App;

/// <summary>Stores connections as a JSON array in the iOS Keychain. Active ID in Preferences.</summary>
public sealed class IosKeychainConnectionStore : IConnectionStore
{
    private const string Service = "OpenAgent";
    private const string Account = "connections";
    private const string ActiveIdKey = "active_connection_id";

    public Task<List<ServerConnection>> LoadAllAsync(CancellationToken ct = default)
    {
        var list = ReadList();
        return Task.FromResult(list);
    }

    public Task<ServerConnection?> LoadActiveAsync(CancellationToken ct = default)
    {
        var list = ReadList();
        if (list.Count == 0) return Task.FromResult<ServerConnection?>(null);
        var activeId = Preferences.Default.Get<string?>(ActiveIdKey, null);
        var match = activeId is not null ? list.Find(c => c.Id == activeId) : null;
        return Task.FromResult<ServerConnection?>(match ?? list[0]);
    }

    public Task SaveAsync(ServerConnection connection, CancellationToken ct = default)
    {
        var list = ReadList();
        var idx = list.FindIndex(c => c.Id == connection.Id);
        if (idx >= 0) list[idx] = connection;
        else list.Add(connection);
        WriteList(list);
        if (!Preferences.Default.ContainsKey(ActiveIdKey))
            Preferences.Default.Set(ActiveIdKey, connection.Id);
        return Task.CompletedTask;
    }

    public Task<int> DeleteAsync(string connectionId, CancellationToken ct = default)
    {
        var list = ReadList();
        list.RemoveAll(c => c.Id == connectionId);
        WriteList(list);
        var activeId = Preferences.Default.Get<string?>(ActiveIdKey, null);
        if (activeId == connectionId)
        {
            if (list.Count > 0)
                Preferences.Default.Set(ActiveIdKey, list[0].Id);
            else
                Preferences.Default.Remove(ActiveIdKey);
        }
        return Task.FromResult(list.Count);
    }

    public Task SetActiveAsync(string connectionId, CancellationToken ct = default)
    {
        Preferences.Default.Set(ActiveIdKey, connectionId);
        return Task.CompletedTask;
    }

    public Task<string?> GetActiveIdAsync(CancellationToken ct = default)
    {
        var id = Preferences.Default.Get<string?>(ActiveIdKey, null);
        return Task.FromResult(id);
    }

    private List<ServerConnection> ReadList()
    {
        var query = NewQuery();
        var result = SecKeyChain.QueryAsData(query, false, out var status);
        if (status != SecStatusCode.Success || result is null) return new List<ServerConnection>();
        var json = NSString.FromData(result, NSStringEncoding.UTF8)?.ToString();
        if (string.IsNullOrEmpty(json)) return new List<ServerConnection>();
        return JsonSerializer.Deserialize<List<ServerConnection>>(json) ?? new List<ServerConnection>();
    }

    private void WriteList(List<ServerConnection> list)
    {
        var json = JsonSerializer.Serialize(list);
        var data = NSData.FromString(json);
        var query = NewQuery();
        var existing = SecKeyChain.QueryAsRecord(query, out _);
        if (existing is not null)
        {
            if (list.Count == 0)
                SecKeyChain.Remove(query);
            else
                SecKeyChain.Update(query, new SecRecord(SecKind.GenericPassword) { ValueData = data });
        }
        else if (list.Count > 0)
        {
            var record = NewQuery();
            record.ValueData = data;
            SecKeyChain.Add(record);
        }
    }

    private static SecRecord NewQuery() => new(SecKind.GenericPassword)
    {
        Service = Service,
        Account = Account
    };
}
