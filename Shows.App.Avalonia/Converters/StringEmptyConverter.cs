using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace Shows.App.Avalonia.Converters;

/// <summary>
/// True when the bound string is null or whitespace — the exact inverse of
/// <see cref="StringNotEmptyConverter"/>. Used to show a placeholder only while a status line is empty.
/// </summary>
/// <remarks>
/// This exists because <see cref="InvertBoolConverter"/> cannot invert a string: it returns
/// <c>value is not true</c>, which is <c>true</c> for any non-empty string, so a placeholder bound through
/// it never hides once a real message arrives and the two lines overlap. Inverting a string needs a
/// string-aware converter.
/// </remarks>
public sealed class StringEmptyConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is not string s || string.IsNullOrWhiteSpace(s);

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
