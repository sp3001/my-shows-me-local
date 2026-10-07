using System;
using System.Globalization;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Avalonia.Styling;

namespace Shows.App.Avalonia.Converters;

/// <summary>
/// Returns the text brush for a category link (improvement 1): the accent brush when the bound category
/// is the one currently selected, otherwise a muted gray. Inputs are the candidate
/// <c>CategoryTab</c> and the page's <c>SelectedCategory</c> (a MultiBinding), so the active link is the
/// only one that lights up. The brushes follow the active theme variant like the rest of the app.
/// </summary>
public sealed class CategorySelectedColorConverter : IMultiValueConverter
{
    private static readonly IBrush PrimaryLight = new SolidColorBrush(Color.Parse("#512BD4"));
    private static readonly IBrush PrimaryDark = new SolidColorBrush(Color.Parse("#AC99EA"));
    private static readonly IBrush GrayLight = new SolidColorBrush(Color.Parse("#6E6E6E"));
    private static readonly IBrush GrayDark = new SolidColorBrush(Color.Parse("#919191"));

    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        var selected = values.Count > 1 ? values[1] : null;
        var isSelected = values.Count > 0 && Equals(values[0], selected);

        // Fully qualified with global::: the bare `Avalonia` would resolve to the enclosing
        // `Shows.App.Avalonia` namespace (CS0234), and `Application` to the sibling `Shows.Application`
        // namespace (CS0118) — the same collisions the App class documents.
        var dark = global::Avalonia.Application.Current?.ActualThemeVariant == ThemeVariant.Dark;

        if (isSelected)
        {
            return dark ? PrimaryDark : PrimaryLight;
        }

        return dark ? GrayDark : GrayLight;
    }
}
