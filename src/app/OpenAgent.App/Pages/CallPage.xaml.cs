using OpenAgent.App.ViewModels;

namespace OpenAgent.App.Pages;

/// <summary>
/// Modal call page hosted over the conversations list. Forwards <see cref="OnAppearing"/> to
/// <see cref="CallViewModel.StartCommand"/> so the session loop kicks off as soon as the page is shown,
/// and disposes the view model on <see cref="OnDisappearing"/> to cancel any in-flight work.
/// </summary>
public partial class CallPage : ContentPage
{
    private readonly CallViewModel _vm;
    public CallPage(CallViewModel vm)
    {
        InitializeComponent();
        BindingContext = _vm = vm;
    }
    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await _vm.StartCommand.ExecuteAsync(null);
    }
    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        _vm.Dispose();
    }
}
