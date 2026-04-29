using ZXing.Net.Maui;
using OpenAgent.App.ViewModels;

namespace OpenAgent.App.Pages;

/// <summary>
/// QR-scan onboarding page. Wires the ZXing camera reader's BarcodesDetected event
/// to <see cref="OnboardingViewModel.OnQrScannedCommand"/>, marshalling the callback
/// to the main thread and using a one-shot latch to guard against rapid re-detection.
/// </summary>
public partial class OnboardingPage : ContentPage
{
    private readonly OnboardingViewModel _vm;
    private bool _handled;

    /// <summary>Creates the page and binds it to the supplied view model.</summary>
    public OnboardingPage(OnboardingViewModel vm)
    {
        InitializeComponent();
        BindingContext = _vm = vm;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        _handled = false;
    }

    private async void OnBarcodesDetected(object? sender, BarcodeDetectionEventArgs e)
    {
        if (_handled) return;
        var text = e.Results?.FirstOrDefault()?.Value;
        if (string.IsNullOrEmpty(text)) return;
        _handled = true;
        await MainThread.InvokeOnMainThreadAsync(() => _vm.OnQrScannedCommand.ExecuteAsync(text));
    }
}
