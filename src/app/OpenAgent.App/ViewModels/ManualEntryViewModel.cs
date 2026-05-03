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
    private readonly IConnectionStore _store;

    /// <summary>Creates a new view model bound to the supplied connection store.</summary>
    public ManualEntryViewModel(IConnectionStore store) => _store = store;

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

        var uri = new Uri(payload!.BaseUrl);
        var conn = new ServerConnection(
            Id: Guid.NewGuid().ToString(),
            Name: uri.Host,
            BaseUrl: payload.BaseUrl,
            Token: payload.Token);

        await _store.SaveAsync(conn);
        await _store.SetActiveAsync(conn.Id);
        await Shell.Current.GoToAsync("//conversations");
    }
}
