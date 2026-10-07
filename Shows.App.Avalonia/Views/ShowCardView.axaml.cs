using Avalonia.Controls;
using Avalonia.Input;
using Shows.App.Avalonia.ViewModels;

namespace Shows.App.Avalonia.Views;

public partial class ShowCardView : UserControl
{
    public ShowCardView()
    {
        InitializeComponent();
    }

    // Avalonia has no tap recognizer (unlike MAUI's TapGestureRecognizer), so the title's PointerPressed
    // invokes the same command the binding would have. The DataContext of the element that raised the
    // event is the card's view model. The title is only rendered as a link when ShowTitleLink is true,
    // so this handler is never reached on the details page where the card suppresses the link.
    private void OnOpenDetails(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Control { DataContext: ShowCardViewModel vm })
        {
            vm.OpenDetailsCommand.Execute(null);
        }
    }
}
