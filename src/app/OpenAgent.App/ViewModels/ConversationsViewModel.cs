using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenAgent.App.Core.Api;
using OpenAgent.App.Core.Models;
using OpenAgent.App.Core.Services;

namespace OpenAgent.App.ViewModels;

/// <summary>
/// View model for the conversations list page. Performs cache-first refresh from
/// <see cref="ConversationCache"/>, then re-fetches via <see cref="IApiClient"/>.
/// On 401 the user is redirected back to onboarding; on transport failures the
/// page falls back to the cached snapshot (offline mode). Also exposes commands
/// for swipe-to-delete, rename, FAB-driven new call, and tap-to-open.
/// </summary>
public partial class ConversationsViewModel : ObservableObject
{
    private readonly IApiClient _api;
    private readonly ConversationCache _cache;

    /// <summary>Conversation rows bound to the page's CollectionView.</summary>
    public ObservableCollection<ConversationListItem> Items { get; } = new();

    /// <summary>True when the agent could not be reached and the list is showing cached data.</summary>
    [ObservableProperty] private bool _isOffline;

    /// <summary>Drives the RefreshView spinner.</summary>
    [ObservableProperty] private bool _isRefreshing;

    /// <summary>Creates a new view model bound to the supplied API client and on-disk cache.</summary>
    public ConversationsViewModel(IApiClient api, ConversationCache cache)
    {
        _api = api;
        _cache = cache;
    }

    /// <summary>Cache-first refresh: paint cached rows immediately, then fetch fresh data and reconcile.
    /// On 401 the user is sent back to the onboarding flow; transport failures fall back to the cache.</summary>
    [RelayCommand]
    public async Task LoadAsync()
    {
        IsRefreshing = true;

        var cached = await _cache.ReadAsync();
        if (cached is not null)
        {
            Replace(cached);
        }

        try
        {
            var fresh = await _api.GetConversationsAsync();
            await _cache.WriteAsync(fresh);
            Replace(fresh);
            IsOffline = false;
        }
        catch (AuthRejectedException)
        {
            await Shell.Current.DisplayAlert("Authentication failed",
                "The agent rejected the API token. Please reconfigure.", "Reconfigure");
            await Shell.Current.GoToAsync("//onboarding");
        }
        catch
        {
            IsOffline = cached is not null;
            if (cached is null)
                await Shell.Current.DisplayAlert("Offline", "Couldn't reach agent.", "OK");
        }
        finally
        {
            IsRefreshing = false;
        }
    }

    /// <summary>Confirms and deletes a conversation via the API, removing it from the list on success.</summary>
    [RelayCommand]
    public async Task DeleteAsync(ConversationListItem item)
    {
        var ok = await Shell.Current.DisplayAlert("Delete?", $"Delete \"{item.Title}\"?", "Delete", "Cancel");
        if (!ok) return;
        try { await _api.DeleteConversationAsync(item.Id); Items.Remove(item); }
        catch { await Shell.Current.DisplayAlert("Failed", "Could not delete.", "OK"); }
    }

    /// <summary>Prompts for a new title and renames the conversation (intention) via the API, then refreshes.</summary>
    [RelayCommand]
    public async Task RenameAsync(ConversationListItem item)
    {
        var name = await Shell.Current.DisplayPromptAsync("Rename", "New title", initialValue: item.Intention ?? item.DisplayName ?? "");
        if (string.IsNullOrWhiteSpace(name)) return;
        try { await _api.RenameConversationAsync(item.Id, name); await LoadAsync(); }
        catch { await Shell.Current.DisplayAlert("Failed", "Could not rename.", "OK"); }
    }

    /// <summary>Starts a new conversation by generating a fresh GUID and navigating to the call page.</summary>
    [RelayCommand]
    public Task NewCallAsync()
    {
        var id = Guid.NewGuid().ToString();
        return Shell.Current.GoToAsync($"call?conversationId={id}&title=New+conversation");
    }

    /// <summary>Opens an existing conversation in the call page.</summary>
    [RelayCommand]
    public Task OpenAsync(ConversationListItem item)
        => Shell.Current.GoToAsync($"call?conversationId={item.Id}&title={Uri.EscapeDataString(item.Title)}");

    /// <summary>Navigates to the settings page.</summary>
    [RelayCommand]
    public Task OpenSettingsAsync() => Shell.Current.GoToAsync("settings");

    private void Replace(IEnumerable<ConversationListItem> fresh)
    {
        Items.Clear();
        foreach (var i in fresh.OrderByDescending(x => x.SortKey))
            Items.Add(i);
    }
}
