using ZXing.Net.Maui;
using OpenAgent.App.ViewModels;

namespace OpenAgent.App.Pages;

/// <summary>
/// QR-scan page. Used for first-time onboarding (navigated via //onboarding) and for adding
/// connections (navigated via "onboarding-add" from settings). The view model's IsAddMode
/// flag controls post-scan navigation.
/// </summary>
[QueryProperty(nameof(IsAddMode), "isAddMode")]
public partial class OnboardingPage : ContentPage
{
    private readonly OnboardingViewModel _vm;
    private bool _handled;

    public string? IsAddMode { get; set; }

    public OnboardingPage(OnboardingViewModel vm)
    {
        InitializeComponent();
        BindingContext = _vm = vm;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        _handled = false;
        _vm.IsAddMode = string.Equals(IsAddMode, "true", StringComparison.OrdinalIgnoreCase);
    }

    private async void OnBarcodesDetected(object? sender, BarcodeDetectionEventArgs e)
    {
        if (_handled) return;
        var text = e.Results?.FirstOrDefault()?.Value;
        if (string.IsNullOrEmpty(text)) return;
        _handled = true;
        await MainThread.InvokeOnMainThreadAsync(() => _vm.OnQrScannedAsync(text));
    }
}
