using Avalonia.Controls;
using Avalonia.Input;
using Shows.App.Avalonia.ViewModels;

namespace Shows.App.Avalonia.Views;

public partial class UpdatesRowView : UserControl
{
    public UpdatesRowView()
    {
        InitializeComponent();
    }

    private void OnOpenDetails(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Control { DataContext: UpdatesRowViewModel vm })
        {
            vm.OpenDetailsCommand.Execute(null);
        }
    }
}
