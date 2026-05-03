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
        Routing.RegisterRoute("onboarding-add", typeof(Pages.OnboardingPage));
    }

    /// <summary>Routes the user to onboarding or conversations based on stored connections.</summary>
    public async Task RouteInitialAsync()
    {
        var services = IPlatformApplication.Current?.Services
                       ?? throw new InvalidOperationException("Service provider not available");
        var store = services.GetRequiredService<IConnectionStore>();
        var active = await store.LoadActiveAsync();
        await GoToAsync(active is null ? "//onboarding" : "//conversations");
    }
}
