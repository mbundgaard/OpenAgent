using OpenAgent.App.ViewModels;

namespace OpenAgent.App.Pages;

/// <summary>
/// Conversations list page. Hosts a <see cref="ConversationsViewModel"/> and triggers
/// its cache-first refresh on every <c>OnAppearing</c> so re-entering the page from
/// a call or settings detour shows up-to-date rows.
/// </summary>
public partial class ConversationsPage : ContentPage
{
    private readonly ConversationsViewModel _vm;

    /// <summary>Creates the page and binds it to the supplied view model.</summary>
    public ConversationsPage(ConversationsViewModel vm)
    {
        InitializeComponent();
        BindingContext = _vm = vm;
    }

    /// <summary>Refreshes the conversation list every time the page becomes visible.</summary>
    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await _vm.LoadCommand.ExecuteAsync(null);
    }
}
