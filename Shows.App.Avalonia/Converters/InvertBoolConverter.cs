using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace Shows.App.Avalonia.Converters;

/// <summary>Boolean inversion for <c>IsVisible</c> bindings (e.g. "show Add when not already tracked").</summary>
public sealed class InvertBoolConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is not true;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is not true;
}
