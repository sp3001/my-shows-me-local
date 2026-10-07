using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Shows.App.Avalonia.ViewModels;

namespace Shows.App.Avalonia.Views;

public partial class SearchView : UserControl
{
    private SearchViewModel? _viewModel;

    public SearchView()
    {
        InitializeComponent();

        DataContextChanged += (_, _) => _viewModel = DataContext as SearchViewModel;
    }

    // The MAUI SearchBar raised TextChanged / SearchButtonPressed; Avalonia's TextBox raises TextChanged
    // and KeyDown. The debounce lives in the ViewModel (OnQueryTextChanged), so the view just forwards.
    // Avalonia's TextChangedEventArgs carries no text, so read the TextBox's current Text.
    private void OnSearchTextChanged(object? sender, TextChangedEventArgs e) =>
        _viewModel?.OnQueryTextChanged((sender as TextBox)?.Text ?? string.Empty);

    private void OnSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && _viewModel is { } vm)
        {
            vm.SearchCommand.Execute(null);
        }
    }
}
