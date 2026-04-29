using System.Text.Json;
using Foundation;
using OpenAgent.App.Core.Onboarding;
using OpenAgent.App.Core.Services;
using Security;

namespace OpenAgent.App;

/// <summary>Stores credentials as a JSON blob in the iOS Keychain. Service "OpenAgent", account "default".</summary>
public sealed class IosKeychainCredentialStore : ICredentialStore
{
    private const string Service = "OpenAgent";
    private const string Account = "default";

    public Task<QrPayload?> LoadAsync(CancellationToken ct = default)
    {
        var query = NewQuery();
        query.ReturnData = true;
        var result = SecKeyChain.QueryAsData(query, false, out var status);
        if (status != SecStatusCode.Success || result is null) return Task.FromResult<QrPayload?>(null);
        var json = result.ToString();
        var payload = JsonSerializer.Deserialize<QrPayload>(json);
        return Task.FromResult(payload);
    }

    public Task SaveAsync(QrPayload payload, CancellationToken ct = default)
    {
        var data = NSData.FromString(JsonSerializer.Serialize(payload));
        var query = NewQuery();
        var existing = SecKeyChain.QueryAsRecord(query, out _);
        if (existing is not null)
        {
            SecKeyChain.Update(query, new SecRecord(SecKind.GenericPassword) { ValueData = data });
        }
        else
        {
            var record = NewQuery();
            record.ValueData = data;
            SecKeyChain.Add(record);
        }
        return Task.CompletedTask;
    }

    public Task ClearAsync(CancellationToken ct = default)
    {
        SecKeyChain.Remove(NewQuery());
        return Task.CompletedTask;
    }

    private static SecRecord NewQuery() => new(SecKind.GenericPassword)
    {
        Service = Service,
        Account = Account
    };
}
