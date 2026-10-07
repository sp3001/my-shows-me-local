using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace Shows.App.Avalonia.Converters;

/// <summary>
/// Drives one star of the clickable rating widget. The bound value is the show's current user rating
/// (<c>int?</c>, null = unrated); the parameter is this star's position (1..10). Returns <c>true</c> when
/// the star should be filled — i.e. its position is at or below the rating — so the view binds it to a
/// style class (<c>Classes.fill</c>) and the brush stays a DynamicResource (theme-aware) in the style
/// rather than being resolved here.
/// </summary>
public sealed class StarFilledConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var rating = value as int? ?? 0;
        return int.TryParse(parameter as string, NumberStyles.Integer, culture, out var star) && star <= rating;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
