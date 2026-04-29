using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenAgent.App.Core.Services;

namespace OpenAgent.App.ViewModels;

/// <summary>
/// Settings screen view-model: shows the persisted server URL, a masked-by-default API token,
/// and the app version, plus a reconfigure escape hatch that clears credentials and routes
/// the user back to the QR-scan onboarding flow.
/// </summary>
public partial class SettingsViewModel : ObservableObject
{
    private readonly ICredentialStore _store;

    /// <summary>Persisted agent base URL loaded from <see cref="ICredentialStore"/>.</summary>
    [ObservableProperty] private string _serverUrl = "";

    /// <summary>Raw API token loaded from <see cref="ICredentialStore"/>. Bound via <see cref="TokenDisplay"/>.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TokenDisplay))]
    private string _token = "";

    /// <summary>When true, <see cref="TokenDisplay"/> returns the raw token; otherwise a fixed-width mask.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TokenDisplay))]
    private bool _showToken;

    /// <summary>Current MAUI app version, sourced from <see cref="AppInfo"/> at construction time.</summary>
    [ObservableProperty] private string _appVersion = AppInfo.Current.VersionString;

    /// <summary>Computed token-cell text: raw token when revealed, otherwise a length-clamped bullet mask.</summary>
    public string TokenDisplay => ShowToken ? Token : new string('•', Math.Max(8, Math.Min(Token.Length, 24)));

    /// <summary>Creates a new settings view-model bound to the supplied credential store.</summary>
    public SettingsViewModel(ICredentialStore store) => _store = store;

    /// <summary>Loads the persisted credentials and populates <see cref="ServerUrl"/> + <see cref="Token"/>.</summary>
    [RelayCommand]
    public async Task LoadAsync()
    {
        var c = await _store.LoadAsync();
        ServerUrl = c?.BaseUrl ?? "";
        Token = c?.Token ?? "";
    }

    /// <summary>Clears all stored credentials and navigates back to the onboarding flow.</summary>
    [RelayCommand]
    public async Task ReconfigureAsync()
    {
        await _store.ClearAsync();
        await Shell.Current.GoToAsync("//onboarding");
    }

    /// <summary>Toggles whether the token is shown in plain text or masked.</summary>
    [RelayCommand]
    public void ToggleReveal() => ShowToken = !ShowToken;
}
