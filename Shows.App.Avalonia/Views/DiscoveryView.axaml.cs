using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Shows.App.Avalonia.ViewModels;

namespace Shows.App.Avalonia.Views;

public partial class DiscoveryView : UserControl
{
    private DiscoveryViewModel? _viewModel;

    public DiscoveryView()
    {
        InitializeComponent();

        DataContextChanged += (_, _) => _viewModel = DataContext as DiscoveryViewModel;

        // The shell calls DiscoveryViewModel.InitializeAsync on first tab selection (the MAUI
        // OnAppearing equivalent), so the first bucket is populated without anything needed here.
    }

    // The name box drives the debounced re-query in the ViewModel (ApplyNameFilter); Avalonia's
    // TextChangedEventArgs carries no text, so read the TextBox's current Text (same as SearchView).
    private void OnNameFilterChanged(object? sender, TextChangedEventArgs e) =>
        _viewModel?.ApplyNameFilter((sender as TextBox)?.Text ?? string.Empty);

    // Enter in any filter box applies the filters immediately (the Search button does the same).
    private void OnFilterKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && _viewModel is { } vm)
        {
            vm.ApplyFiltersCommand.Execute(null);
        }
    }
}
