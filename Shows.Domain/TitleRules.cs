namespace Shows.Domain;

/// <summary>
/// How a show or episode title is presented (SPEC R3).
/// </summary>
/// <param name="Primary">The localized title when there is one, otherwise the original.</param>
/// <param name="Secondary">
/// The original title, shown beneath <paramref name="Primary"/> — or null when it would duplicate it,
/// which is the case whenever there is no localized title.
/// </param>
public readonly record struct TitlePair(string Primary, string? Secondary);

/// <summary>
/// SPEC R3, as a pure function so the rule is testable without a database or a view.
/// </summary>
public static class TitleRules
{
    /// <summary>
    /// If a localized title exists it is primary with the original beneath; otherwise the original
    /// alone is primary.
    /// </summary>
    /// <remarks>
    /// A whitespace-only localized title counts as absent: the import trims the export's titles
    /// (ADR-015) and an untrimmed field can arrive as <c>" "</c>, which would render as a blank
    /// heading over the real title.
    /// </remarks>
    public static TitlePair Select(string original, string? localized)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(original);

        var hasLocalized = !string.IsNullOrWhiteSpace(localized);

        return hasLocalized
            ? new TitlePair(localized!, original)
            : new TitlePair(original, null);
    }
}
