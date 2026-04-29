namespace OpenAgent.App;

public partial class App : Application
{
    public App() => InitializeComponent();

    protected override Window CreateWindow(IActivationState? activationState)
    {
        var shell = new AppShell();
        var window = new Window(shell);
        window.Created += async (_, _) => await shell.RouteInitialAsync();
        return window;
    }
}
