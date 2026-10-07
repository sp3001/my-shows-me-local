using Avalonia.Controls;
using Avalonia.Input;
using Shows.App.Avalonia.ViewModels;

namespace Shows.App.Avalonia.Views;

public partial class ShowDetailsView : UserControl
{
    public ShowDetailsView()
    {
        InitializeComponent();

        // The page is loaded by NavigationService right after it is pushed (vm.LoadAsync(id)), so there is
        // no OnAppearing equivalent to wire here — the id arrives through the navigation route, not a query
        // attribute.
    }

    // The whole episode row is the watch toggle (MAUI's row tap gesture). The checkbox is display-only.
    private void OnToggleEpisode(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Control { DataContext: EpisodeRowViewModel vm })
        {
            vm.ToggleWatchedCommand.Execute(null);
        }
    }

    // A rating star tap. The star's Tag carries its position (1..10); the VM decides set-vs-clear (clicking
    // the star at the current rating clears it). Avalonia has no tap recognizer, so PointerPressed drives
    // the command directly, matching the card/episode handlers.
    private void OnSetRating(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Control { DataContext: ShowDetailsViewModel vm } star
            && int.TryParse(star.Tag as string, out var value))
        {
            vm.SetRatingCommand.Execute(value);
        }
    }
}
