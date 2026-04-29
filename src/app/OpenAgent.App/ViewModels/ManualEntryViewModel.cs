using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenAgent.App.Core.Onboarding;
using OpenAgent.App.Core.Services;

namespace OpenAgent.App.ViewModels;

/// <summary>
/// View model for the manual server-URL + token entry page. Constructs a probe URL
/// of the form <c>{ServerUrl}/?token={Token}</c> and routes it through
/// <see cref="QrPayloadParser"/> so validation rules match the QR path exactly.
/// </summary>
public partial class ManualEntryViewModel : ObservableObject
{
    private readonly ICredentialStore _store;

    /// <summary>Creates a new view model bound to the supplied credential store.</summary>
    public ManualEntryViewModel(ICredentialStore store) => _store = store;

    [ObservableProperty] private string _serverUrl = "";
    [ObservableProperty] private string _token = "";
    [ObservableProperty] private string? _error;
    [ObservableProperty] private bool _hasError;

    /// <summary>Validates and persists the entered credentials, then navigates to the conversations root.</summary>
    [RelayCommand]
    public async Task SaveAsync()
    {
        var probe = $"{ServerUrl.TrimEnd('/')}/?token={Token}";
        if (!QrPayloadParser.TryParse(probe, out var payload, out var err))
        {
            Error = err;
            HasError = true;
            return;
        }
        await _store.SaveAsync(payload!);
        await Shell.Current.GoToAsync("//conversations");
    }
}
