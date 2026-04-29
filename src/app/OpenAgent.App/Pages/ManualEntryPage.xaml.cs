using OpenAgent.App.ViewModels;

namespace OpenAgent.App.Pages;

/// <summary>
/// Manual server-URL + token entry page. Pure view — all logic lives in
/// <see cref="ManualEntryViewModel"/>.
/// </summary>
public partial class ManualEntryPage : ContentPage
{
    /// <summary>Creates the page and binds it to the supplied view model.</summary>
    public ManualEntryPage(ManualEntryViewModel vm)
    {
        InitializeComponent();
        BindingContext = vm;
    }
}
