using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
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
    //
    // The row's info button lives INSIDE the row, so its press bubbles here too — and a press meant to read
    // a synopsis would silently un-watch the episode. The press is ignored when it started inside a button,
    // which leaves the button free to open its own flyout (no event is swallowed, so the flyout still works).
    private void OnToggleEpisode(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source is Visual source && source.FindAncestorOfType<Button>(includeSelf: true) is not null)
        {
            return;
        }

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
