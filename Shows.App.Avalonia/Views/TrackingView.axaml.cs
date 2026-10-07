using Avalonia.Controls;
using Avalonia.Input;
using Shows.App.Avalonia.ViewModels;

namespace Shows.App.Avalonia.Views;

public partial class TrackingView : UserControl
{
    public TrackingView()
    {
        InitializeComponent();
    }

    // Category link tap (improvement 1): select the tab, which reloads it. The MAUI view drove this via a
    // TapGestureRecognizer bound to the page VM's command; here the handler reads the page VM from the
    // UserControl's DataContext and the tapped CategoryTab from the label's DataContext.
    private void OnSelectCategory(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is TrackingViewModel vm
            && sender is Control { DataContext: CategoryTab tab })
        {
            vm.SelectCategoryCommand.Execute(tab);
        }
    }
}
