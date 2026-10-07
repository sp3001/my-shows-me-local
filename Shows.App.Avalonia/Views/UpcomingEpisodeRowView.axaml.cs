using Avalonia.Controls;
using Avalonia.Input;
using Shows.App.Avalonia.ViewModels;

namespace Shows.App.Avalonia.Views;

public partial class UpcomingEpisodeRowView : UserControl
{
    public UpcomingEpisodeRowView()
    {
        InitializeComponent();
    }

    private void OnOpenDetails(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Control { DataContext: UpcomingEpisodeRowViewModel vm })
        {
            vm.OpenDetailsCommand.Execute(null);
        }
    }
}
