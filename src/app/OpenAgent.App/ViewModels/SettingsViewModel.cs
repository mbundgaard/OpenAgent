using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenAgent.App.Core.Services;

namespace OpenAgent.App.ViewModels;

/// <summary>
/// Settings page with connections management: lists all server connections with name + URL,
/// supports rename (tap), delete (swipe), and add (navigates to QR scan). Also shows app version.
/// </summary>
public partial class SettingsViewModel : ObservableObject
{
    private readonly IConnectionStore _store;
    private readonly ConversationCache _cache;

    /// <summary>All stored server connections.</summary>
    public ObservableCollection<ServerConnection> Connections { get; } = new();

    /// <summary>Id of the currently active connection, used for visual highlight.</summary>
    [ObservableProperty] private string? _activeConnectionId;

    /// <summary>Current MAUI app version.</summary>
    [ObservableProperty] private string _appVersion = AppInfo.Current.VersionString;

    /// <summary>Creates a new settings view-model.</summary>
    public SettingsViewModel(IConnectionStore store, ConversationCache cache)
    {
        _store = store;
        _cache = cache;
    }

    /// <summary>Loads all connections and the active Id.</summary>
    [RelayCommand]
    public async Task LoadAsync()
    {
        var all = await _store.LoadAllAsync();
        ActiveConnectionId = await _store.GetActiveIdAsync();
        Connections.Clear();
        foreach (var c in all) Connections.Add(c);
    }

    /// <summary>Prompts for a new name and renames the connection.</summary>
    [RelayCommand]
    public async Task RenameConnectionAsync(ServerConnection connection)
    {
        var name = await Shell.Current.DisplayPromptAsync("Rename connection", "Name", initialValue: connection.Name);
        if (string.IsNullOrWhiteSpace(name)) return;
        var updated = connection with { Name = name.Trim() };
        await _store.SaveAsync(updated);
        await LoadAsync();
    }

    /// <summary>Confirms and deletes a connection. If active, switches to next. If none left, goes to onboarding.</summary>
    [RelayCommand]
    public async Task DeleteConnectionAsync(ServerConnection connection)
    {
        var ok = await Shell.Current.DisplayAlert("Delete connection?",
            $"Delete \"{connection.Name}\"? This cannot be undone.", "Delete", "Cancel");
        if (!ok) return;

        var remaining = await _store.DeleteAsync(connection.Id);
        _cache.DeleteCache(connection.Id);

        if (remaining == 0)
        {
            await Shell.Current.GoToAsync("//onboarding");
            return;
        }

        await LoadAsync();

        if (connection.Id == ActiveConnectionId)
        {
            await Shell.Current.GoToAsync("//conversations");
        }
    }

    /// <summary>Navigates to the QR scan page in add-connection mode.</summary>
    [RelayCommand]
    public Task AddConnectionAsync() => Shell.Current.GoToAsync("onboarding-add?isAddMode=true");
}
