using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenAgent.App.Core.Onboarding;
using OpenAgent.App.Core.Services;

namespace OpenAgent.App.ViewModels;

/// <summary>
/// View model for the QR-scan onboarding page. Parses scanned payloads via
/// <see cref="QrPayloadParser"/> and persists the credentials to <see cref="ICredentialStore"/>;
/// the "Enter manually" command navigates to <c>ManualEntryPage</c> instead.
/// </summary>
public partial class OnboardingViewModel : ObservableObject
{
    private readonly ICredentialStore _store;

    /// <summary>Creates a new view model bound to the supplied credential store.</summary>
    public OnboardingViewModel(ICredentialStore store) => _store = store;

    [ObservableProperty] private string? _error;
    [ObservableProperty] private bool _hasError;

    /// <summary>Handles a scanned QR payload: parses, persists, then navigates to the conversations root.</summary>
    [RelayCommand]
    public async Task OnQrScannedAsync(string text)
    {
        if (!QrPayloadParser.TryParse(text, out var payload, out var err))
        {
            Error = err;
            HasError = true;
            return;
        }
        await _store.SaveAsync(payload!);
        await Shell.Current.GoToAsync("//conversations");
    }

    /// <summary>Pushes the manual-entry page onto the navigation stack.</summary>
    [RelayCommand]
    public Task OpenManualEntryAsync() => Shell.Current.GoToAsync("manual-entry");
}
