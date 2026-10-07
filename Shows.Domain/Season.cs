namespace Shows.Domain;

/// <summary>
/// A grouping entity holding the metadata that belongs to a season rather than to a show or an
/// episode.
/// </summary>
/// <remarks>
/// It has <b>no external identifier</b> (ADR-025): neither TVmaze nor IMDb gives a season a stable id
/// that survives a re-fetch, so a property claiming one would be a promise nothing can keep.
/// <c>SeasonNumber == 0</c> means specials.
/// </remarks>
public sealed class Season
{
    /// <summary>Client-generated surrogate key (SPEC R13). Never re-minted — <c>ui_season_state</c> points at it.</summary>
    public long Id { get; set; }

    public long ShowId { get; set; }

    /// <summary>0 = specials.</summary>
    public int SeasonNumber { get; set; }

    public string? Name { get; set; }

    /// <summary>A cache of a count over <see cref="Episodes"/>, not an authority.</summary>
    public int? EpisodeCount { get; set; }

    public string? PosterUrl { get; set; }

    public List<Episode> Episodes { get; } = [];
}
