namespace Shows.Domain;

/// <summary>
/// One recorded disagreement between TVmaze (authoritative) and IMDb (enrichment) about a single
/// episode of a show — the unit the details page surfaces so the user can see what IMDb offered that
/// TVmaze did not keep (ADR-031).
/// </summary>
/// <remarks>
/// <para>
/// This is a <b>derived, fully rebuildable</b> row (SCHEMA §4a): the <c>Enrichment --imdb</c> mode is its
/// sole writer and wipes/recomputes a show's rows each run, so it never drifts and holds nothing the user
/// created. The UI cannot read the <c>imdb_*</c> source tables (rule 5 / SPEC R11), so the mapper
/// materialises both sides here — <see cref="TvmazeValue"/> is what was kept, <see cref="ImdbValue"/> is
/// what was rejected.
/// </para>
/// <para>
/// <see cref="Kind"/> is a closed vocabulary (see SCHEMA §4a): <c>imdb_only</c>, <c>tvmaze_only</c>,
/// <c>title</c>, <c>runtime</c>, <c>air_date_gap</c>, <c>description_gap</c>. It is a plain string, not an
/// enum-over-id, because the schema stores it as TEXT and the mapper is the only producer — a vocabulary
/// table would add a join for a set that never changes at runtime.
/// </para>
/// <para>
/// <see cref="EpisodeId"/> is null for an <c>imdb_only</c> row: IMDb knows a (season, episode) TVmaze
/// never created, so there is no <c>episode</c> row to point at. The denormalised
/// <see cref="SeasonNumber"/>/<see cref="EpisodeNumber"/> let the details page label such a row anyway.
/// </para>
/// </remarks>
public sealed class EpisodeMismatch
{
    /// <summary>Client-generated surrogate key (SPEC R13). Not referenced by any user table — the row is rebuildable.</summary>
    public long Id { get; set; }

    public long ShowId { get; set; }

    /// <summary>Null for an <c>imdb_only</c> row (no <c>episode</c> row exists for it).</summary>
    public long? EpisodeId { get; set; }

    /// <summary>One of the SCHEMA §4a kinds. Closed vocabulary, produced only by the enrichment mapper.</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>The value TVmaze kept (authoritative). Null when TVmaze had no value (a gap) or none applies.</summary>
    public string? TvmazeValue { get; set; }

    /// <summary>The value IMDb offered and was rejected. Null for a <c>tvmaze_only</c> row or a gap IMDb cannot fill.</summary>
    public string? ImdbValue { get; set; }

    /// <summary>Denormalised so an <c>imdb_only</c> row (no episode) can still be labelled "S02E05".</summary>
    public int? SeasonNumber { get; set; }

    public int? EpisodeNumber { get; set; }
}
