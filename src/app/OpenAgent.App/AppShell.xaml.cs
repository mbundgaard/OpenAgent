using OpenAgent.App.Core.Services;

namespace OpenAgent.App;

public partial class AppShell : Shell
{
    public AppShell()
    {
        InitializeComponent();
        Routing.RegisterRoute("call", typeof(Pages.CallPage));
        Routing.RegisterRoute("settings", typeof(Pages.SettingsPage));
        Routing.RegisterRoute("manual-entry", typeof(Pages.ManualEntryPage));
    }

    /// <summary>Routes the user to onboarding or the conversations list based on stored credentials.
    /// Called from App.xaml.cs after MainPage is set; uses IPlatformApplication.Current.Services
    /// rather than this.Handler.MauiContext (which can be null when Loaded fires).</summary>
    public async Task RouteInitialAsync()
    {
        var services = IPlatformApplication.Current?.Services
                       ?? throw new InvalidOperationException("Service provider not available");
        var store = services.GetRequiredService<ICredentialStore>();
        var creds = await store.LoadAsync();
        await GoToAsync(creds is null ? "//onboarding" : "//conversations");
    }
}
