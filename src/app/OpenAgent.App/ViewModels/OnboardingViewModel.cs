using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OpenAgent.App.Core.Onboarding;
using OpenAgent.App.Core.Services;

namespace OpenAgent.App.ViewModels;

/// <summary>
/// View model for the QR-scan onboarding page. Parses scanned payloads via
/// <see cref="QrPayloadParser"/>, creates a <see cref="ServerConnection"/>,
/// and persists it to <see cref="IConnectionStore"/>.
/// </summary>
public partial class OnboardingViewModel : ObservableObject
{
    private readonly IConnectionStore _store;
    private readonly ILogger<OnboardingViewModel> _logger;

    /// <summary>When true, the QR scan was triggered from Settings (add connection) rather than first-time onboarding.</summary>
    [ObservableProperty] private bool _isAddMode;

    /// <summary>Creates a new view model bound to the supplied connection store.</summary>
    public OnboardingViewModel(IConnectionStore store, ILogger<OnboardingViewModel>? logger = null)
    {
        _store = store;
        _logger = logger ?? NullLogger<OnboardingViewModel>.Instance;
    }

    [ObservableProperty] private string? _error;
    [ObservableProperty] private bool _hasError;

    /// <summary>Handles a scanned QR payload: parses, creates connection, then navigates.</summary>
    [RelayCommand]
    public async Task OnQrScannedAsync(string text)
    {
        if (!QrPayloadParser.TryParse(text, out var payload, out var err))
        {
            _logger.LogWarning("QR parse failed (len={Len}): {Error}", text?.Length ?? 0, err);
            Error = err;
            HasError = true;
            return;
        }

        var uri = new Uri(payload!.BaseUrl);
        _logger.LogInformation("QR parsed ok host={Host}", uri.Host);

        var conn = new ServerConnection(
            Id: Guid.NewGuid().ToString(),
            Name: uri.Host,
            BaseUrl: payload.BaseUrl,
            Token: payload.Token);

        await _store.SaveAsync(conn);
        await _store.SetActiveAsync(conn.Id);

        if (IsAddMode)
            await Shell.Current.GoToAsync("..");
        else
            await Shell.Current.GoToAsync("//conversations");
    }

    /// <summary>Pushes the manual-entry page onto the navigation stack.</summary>
    [RelayCommand]
    public Task OpenManualEntryAsync() => Shell.Current.GoToAsync("manual-entry");
}
