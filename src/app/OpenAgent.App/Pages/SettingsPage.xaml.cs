using System.Globalization;
using OpenAgent.App.ViewModels;

namespace OpenAgent.App.Pages;

/// <summary>Settings page with connections management.</summary>
public partial class SettingsPage : ContentPage
{
    private readonly SettingsViewModel _vm;

    public SettingsPage(SettingsViewModel vm)
    {
        InitializeComponent();
        BindingContext = _vm = vm;
        Resources.Add("IsActiveConverter", new IsActiveConnectionConverter(_vm));
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await _vm.LoadCommand.ExecuteAsync(null);
    }
}

/// <summary>Returns true when the bound connection Id matches the active connection Id on the view model.</summary>
internal sealed class IsActiveConnectionConverter : IValueConverter
{
    private readonly SettingsViewModel _vm;
    public IsActiveConnectionConverter(SettingsViewModel vm) => _vm = vm;

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is string id && id == _vm.ActiveConnectionId;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
