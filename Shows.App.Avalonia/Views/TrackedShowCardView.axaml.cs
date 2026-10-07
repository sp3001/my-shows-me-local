using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Shows.App.Avalonia.ViewModels;

namespace Shows.App.Avalonia.Views;

public partial class TrackedShowCardView : UserControl
{
    public TrackedShowCardView()
    {
        InitializeComponent();

        // The card loads its seasons the first time it is bound (issue 7: nothing is fetched at list-render
        // time, and there is no user "Episodes" toggle to trigger it). DataContextChanged is Avalonia's
        // analogue of MAUI's OnBindingContextChanged — it fires when the list assigns a card VM.
        // EnsureLoadedAsync is idempotent and short-circuits for non-Started categories, so a non-tracker
        // card never queries.
        DataContextChanged += (_, _) =>
        {
            if (DataContext is TrackedShowCardViewModel vm)
            {
                _ = vm.EnsureLoadedAsync();
            }
        };
    }

    // The MAUI views drove these via TapGestureRecognizer; Avalonia has no tap recognizer, so the
    // handlers invoke the same commands the bindings would have. The DataContext of the element that
    // raised the event is the relevant view model (the card, season, or episode row).

    private void OnOpenDetails(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Control { DataContext: TrackedShowCardViewModel vm })
        {
            vm.OpenDetailsCommand.Execute(null);
        }
    }

    private void OnTogglePin(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Control { DataContext: TrackedShowCardViewModel vm })
        {
            vm.TogglePinCommand.Execute(null);
        }
    }

    private void OnToggleSeason(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Control { DataContext: SeasonGroupViewModel vm })
        {
            vm.ToggleCommand.Execute(null);
        }
    }

    private void OnToggleEpisode(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Control { DataContext: EpisodeRowViewModel vm })
        {
            vm.ToggleWatchedCommand.Execute(null);
        }
    }
}
