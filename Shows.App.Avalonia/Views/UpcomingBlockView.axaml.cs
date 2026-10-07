using Avalonia.Controls;
using Shows.App.Avalonia.ViewModels;

namespace Shows.App.Avalonia.Views;

public partial class UpcomingBlockView : UserControl
{
    public UpcomingBlockView()
    {
        InitializeComponent();
    }

    // The "This Week" block gets a neutral gray tint; "Other" stays transparent. Applied on bind and
    // whenever the DataContext is re-set (a Border background is not bound here so the two blocks can
    // share one view).
    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        ApplyHighlight();
    }

    private void ApplyHighlight()
    {
        if (DataContext is UpcomingBlockViewModel vm)
        {
            BlockBorder.Classes.Set("highlight", vm.IsHighlighted);
        }
    }
}
