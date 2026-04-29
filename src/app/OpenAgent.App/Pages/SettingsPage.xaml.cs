using OpenAgent.App.ViewModels;

namespace OpenAgent.App.Pages;

/// <summary>
/// Read-only display of the agent's server URL, masked API token, and app version, with a
/// destructive reconfigure button that clears credentials and routes back to onboarding.
/// </summary>
public partial class SettingsPage : ContentPage
{
    private readonly SettingsViewModel _vm;

    /// <summary>Creates the page and binds it to the supplied view model.</summary>
    public SettingsPage(SettingsViewModel vm)
    {
        InitializeComponent();
        BindingContext = _vm = vm;
    }

    /// <summary>Reloads credentials every time the page becomes visible so freshly-saved values appear.</summary>
    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await _vm.LoadCommand.ExecuteAsync(null);
    }
}
