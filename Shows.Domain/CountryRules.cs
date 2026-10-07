namespace Shows.Domain;

/// <summary>
/// The one place that decides what a show's <b>country</b> label is (improvement 21).
/// </summary>
/// <remarks>
/// <para>
/// TVmaze carries a country only <em>inside</em> the broadcaster — <c>network.country</c> for a broadcast
/// network, <c>webChannel.country</c> for a streaming service — and a streaming broadcaster very often
/// carries nothing at all: <c>"webChannel": { "name": "Netflix", "country": null }</c> is the normal shape.
/// <c>language</c>, by contrast, is filled for nearly every show. So a Korean drama on Netflix (tvmaze
/// 84377, "Dead-End Job") has no country anywhere in its payload while its language says exactly what the
/// user wants to see and filter by.
/// </para>
/// <para>
/// The rule is therefore: <b>broadcaster country name → broadcaster country code → language</b>, first
/// non-blank wins. Falling back to the language is a substitution of a coarser facet for a missing one,
/// not a guess at a fact: "Korean" is what TVmaze itself says about the show.
/// </para>
/// <para>
/// This lives in Domain because the rule must be identical on every path that shows or filters a country —
/// the tracked mapping, the search read model, and the Discovery facets. Any UI or provider that
/// re-implemented it would drift, and the drift would be invisible: the same show would read "Korean" on
/// one page and nothing on another.
/// </para>
/// </remarks>
public static class CountryRules
{
    /// <summary>
    /// The country to display and to filter on. Null only when TVmaze has no country <em>and</em> no
    /// language, in which case the show genuinely has no country facet and belongs under "Not set".
    /// </summary>
    /// <param name="countryName"><c>country.name</c> from the broadcaster, e.g. "United States".</param>
    /// <param name="countryCode">The two-letter <c>country.code</c>, e.g. "US". Used when the name is absent.</param>
    /// <param name="language">TVmaze <c>language</c>, e.g. "Korean". The fallback.</param>
    public static string? Display(string? countryName, string? countryCode, string? language) =>
        NonBlank(countryName) ?? NonBlank(countryCode) ?? NonBlank(language);

    // A blank counts as absent: TVmaze returns "" for a field it has decided not to fill, and an empty
    // string would win the coalescing chain and render as nothing.
    private static string? NonBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
