using System.Globalization;

namespace Shows.App.Avalonia.Services;

/// <summary>
/// Turns a runtime in <b>minutes</b> into the text the episode and season rows show. The stored unit is
/// minutes (<c>episode.runtime_minutes</c>, TVmaze <c>runtime</c>); the formatting lives here so the two
/// places that need it — a single episode's "42 min" and a season's "3h 25m" total — cannot drift apart.
/// </summary>
/// <remarks>
/// Presentation only, so it sits in the UI layer rather than Domain: the Domain entity carries the number,
/// and no business rule reads the string. Null (the provider has no runtime) returns null so the caller
/// hides the text entirely rather than rendering "0 min".
/// </remarks>
public static class RuntimeFormat
{
    /// <summary>One episode's runtime: <c>42 min</c>. Null when unknown.</summary>
    public static string? Episode(int? minutes) =>
        minutes is > 0 ? $"{minutes.Value.ToString(CultureInfo.InvariantCulture)} min" : null;

    /// <summary>
    /// A total runtime, compact: <c>3h 25m</c>, <c>2h</c> or <c>45m</c>. Null when unknown or zero, so a
    /// season whose episodes carry no runtime shows no duration at all.
    /// </summary>
    public static string? Total(int? minutes)
    {
        if (minutes is not > 0)
        {
            return null;
        }

        var (h, m) = ((int)minutes.Value / 60, minutes.Value % 60);
        if (h == 0)
        {
            return $"{m.ToString(CultureInfo.InvariantCulture)}m";
        }

        return m == 0
            ? $"{h.ToString(CultureInfo.InvariantCulture)}h"
            : $"{h.ToString(CultureInfo.InvariantCulture)}h {m.ToString(CultureInfo.InvariantCulture)}m";
    }
}
