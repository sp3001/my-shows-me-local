using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace Shows.App.Avalonia.Converters;

/// <summary>
/// True when the bound string is non-null and non-empty. Used to hide the separator spans in the card's
/// meta line when the year/status/rating they precede are absent — a dangling "· ★" with no value after it
/// is the visible bug this prevents.
/// </summary>
public sealed class StringNotEmptyConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string s && !string.IsNullOrWhiteSpace(s);

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
