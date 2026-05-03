using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenAgent.App.Core.Api;
using OpenAgent.App.Core.Models;
using OpenAgent.App.Core.Services;

namespace OpenAgent.App.ViewModels;

/// <summary>
/// Conversations list with a top-bar connection picker. Loads conversations for the active
/// connection, supports switching via the picker, and provides swipe actions.
/// </summary>
public partial class ConversationsViewModel : ObservableObject
{
    private readonly IApiClient _api;
    private readonly IConnectionStore _connectionStore;
    private readonly ConversationCache _cache;
    private bool _suppressPickerChange;

    /// <summary>Conversation rows bound to the CollectionView.</summary>
    public ObservableCollection<ConversationListItem> Items { get; } = new();

    /// <summary>Available connections for the top-bar picker.</summary>
    public ObservableCollection<ServerConnection> Connections { get; } = new();

    /// <summary>Currently selected connection in the picker.</summary>
    [ObservableProperty] private ServerConnection? _selectedConnection;

    [ObservableProperty] private bool _isOffline;
    [ObservableProperty] private bool _isRefreshing;

    public ConversationsViewModel(IApiClient api, IConnectionStore connectionStore, ConversationCache cache)
    {
        _api = api;
        _connectionStore = connectionStore;
        _cache = cache;
    }

    /// <summary>Loads connections into the picker and refreshes conversations for the active one.</summary>
    [RelayCommand]
    public async Task LoadAsync()
    {
        IsRefreshing = true;

        var all = await _connectionStore.LoadAllAsync();
        var active = await _connectionStore.LoadActiveAsync();

        _suppressPickerChange = true;
        Connections.Clear();
        foreach (var c in all) Connections.Add(c);
        SelectedConnection = active is not null ? Connections.FirstOrDefault(c => c.Id == active.Id) : Connections.FirstOrDefault();
        _suppressPickerChange = false;

        if (active is null)
        {
            IsRefreshing = false;
            return;
        }

        await RefreshConversationsAsync(active.Id);
        IsRefreshing = false;
    }

    /// <summary>Called when the picker selection changes. Switches active connection and reloads.</summary>
    partial void OnSelectedConnectionChanged(ServerConnection? value)
    {
        if (_suppressPickerChange || value is null) return;
        _ = SwitchConnectionAsync(value);
    }

    private async Task SwitchConnectionAsync(ServerConnection connection)
    {
        await _connectionStore.SetActiveAsync(connection.Id);
        IsRefreshing = true;
        await RefreshConversationsAsync(connection.Id);
        IsRefreshing = false;
    }

    private async Task RefreshConversationsAsync(string connectionId)
    {
        var cached = await _cache.ReadAsync(connectionId);
        if (cached is not null) Replace(cached);

        try
        {
            var fresh = await _api.GetConversationsAsync();
            await _cache.WriteAsync(connectionId, fresh);
            Replace(fresh);
            IsOffline = false;
        }
        catch (AuthRejectedException)
        {
            await Shell.Current.DisplayAlert("Authentication failed",
                "The agent rejected the API token. Please reconfigure.", "OK");
            await Shell.Current.GoToAsync("settings");
        }
        catch
        {
            IsOffline = cached is not null;
            if (cached is null)
                await Shell.Current.DisplayAlert("Offline", "Couldn't reach agent.", "OK");
        }
    }

    [RelayCommand]
    public async Task DeleteAsync(ConversationListItem item)
    {
        var ok = await Shell.Current.DisplayAlert("Delete?", $"Delete \"{item.Title}\"?", "Delete", "Cancel");
        if (!ok) return;
        try { await _api.DeleteConversationAsync(item.Id); Items.Remove(item); }
        catch { await Shell.Current.DisplayAlert("Failed", "Could not delete.", "OK"); }
    }

    [RelayCommand]
    public async Task RenameAsync(ConversationListItem item)
    {
        var name = await Shell.Current.DisplayPromptAsync("Rename", "New title", initialValue: item.Intention ?? item.DisplayName ?? "");
        if (string.IsNullOrWhiteSpace(name)) return;
        try { await _api.RenameConversationAsync(item.Id, name); await LoadAsync(); }
        catch { await Shell.Current.DisplayAlert("Failed", "Could not rename.", "OK"); }
    }

    [RelayCommand]
    public Task NewCallAsync()
    {
        var id = Guid.NewGuid().ToString();
        return Shell.Current.GoToAsync($"call?conversationId={id}&title=New+conversation");
    }

    [RelayCommand]
    public Task OpenAsync(ConversationListItem item)
        => Shell.Current.GoToAsync($"call?conversationId={item.Id}&title={Uri.EscapeDataString(item.Title)}");

    [RelayCommand]
    public Task OpenSettingsAsync() => Shell.Current.GoToAsync("settings");

    private void Replace(IEnumerable<ConversationListItem> fresh)
    {
        Items.Clear();
        foreach (var i in fresh.OrderByDescending(x => x.SortKey))
            Items.Add(i);
    }
}
