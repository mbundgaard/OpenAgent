namespace OpenAgent.App;

public partial class App : Application
{
    public App()
    {
        InitializeComponent();
        var shell = new AppShell();
        MainPage = shell;
        Dispatcher.Dispatch(async () => await shell.RouteInitialAsync());
    }
}
