using Avalonia.Controls;

namespace Shows.App.Avalonia.Views;

public partial class SearchDetailsView : UserControl
{
    public SearchDetailsView()
    {
        InitializeComponent();

        // The page is loaded by NavigationService right after it is pushed (vm.LoadAsync(tvmazeId)), so
        // there is no OnAppearing / query-attribute wiring here — the id arrives through the navigation
        // route.
    }
}
