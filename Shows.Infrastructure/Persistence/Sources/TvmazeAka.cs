namespace Shows.Infrastructure.Persistence.Sources;

/// <summary>
/// One alternative title from TVmaze <c>/shows/{id}/akas</c>, stored verbatim (SCHEMA.md §3, step 7.5).
/// Read only by the mapper (SPEC R11/P3). The endpoint keys on <b>country</b>, not language — there is no
/// <c>language</c> field (probed live 2026-09-30) — so the pick is by <see cref="CountryCode"/>.
/// </summary>
/// <remarks>
/// The composite PK is <c>(tvmaze_show_id, country_code, name)</c>: TVmaze returns several titles per
/// country, so <c>country_code</c> alone is not unique per show. <see cref="AkaOrder"/> preserves the API's
/// array order, which is the order to trust — the mapper picks the lowest for the target country as
/// <c>tracked_show.title_localized</c>.
/// </remarks>
public sealed class TvmazeAka
{
    public int TvmazeShowId { get; set; }

    /// <summary><c>country.code</c> — 'RU', 'CN', ... The pick key.</summary>
    public string CountryCode { get; set; } = string.Empty;

    /// <summary><c>country.name</c>, verbatim.</summary>
    public string? CountryName { get; set; }

    /// <summary>The aka title, verbatim.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Index in the API array; the pick is the lowest for the target country.</summary>
    public int AkaOrder { get; set; }

    public DateTimeOffset FetchedAt { get; set; }
}
