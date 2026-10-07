namespace Shows.Application;

/// <summary>
/// The full details for an <b>untracked</b> catalog show (user-issues 19 follow-up): the show read model
/// plus its episode list grouped by season. This is what the details page for a search or Discovery hit
/// renders — the show has no <c>tracked_show</c> row yet (SPEC P2/R8a), so it cannot go through
/// <see cref="Shows.Domain"/> details; it reads the catalog instead.
/// </summary>
/// <remarks>
/// The two producers differ only in <em>where</em> the episodes come from, and that is invisible here:
/// a Discovery show fills <see cref="Seasons"/> from the cached <c>/schedule/full</c> snapshot (no fetch),
/// while a Search show fills it from a live <c>/shows/{id}/episodes</c> call. The page renders either the
/// same way. Episodes are grouped so the page shows season headers, matching the tracked-details layout.
/// </remarks>
public sealed record CatalogDetails(CatalogShow Show, IReadOnlyList<CatalogSeason> Seasons);

/// <summary>One season's worth of catalog episodes, in ascending episode-number order.</summary>
/// <param name="SeasonNumber">The season number; <c>0</c> is the specials season.</param>
/// <param name="Episodes">The season's episodes, ordered by number (nulls last).</param>
public sealed record CatalogSeason(int SeasonNumber, IReadOnlyList<CatalogEpisode> Episodes);

/// <summary>
/// One episode as the catalog knows it, shaped for the untracked-details list. Deliberately narrower than
/// <see cref="EpisodeCatalogInfo"/> (which carries raw provider strings for the mapper): this is a display
/// read model, so the type is already reduced to <see cref="IsSpecial"/> and the air date to a
/// <see cref="DateOnly"/>.
/// </summary>
/// <param name="SeasonNumber">The season this episode belongs to; <c>0</c> is the specials season.</param>
/// <param name="Number">The episode number within its season; null for an unnumbered special.</param>
/// <param name="Title">The episode title, as the provider gave it.</param>
/// <param name="AirDate">The air date when known.</param>
/// <param name="IsSpecial">True for a special (season 0 / type "special") — rendered with a marker, not a number.</param>
public sealed record CatalogEpisode(int SeasonNumber, int? Number, string? Title, DateOnly? AirDate, bool IsSpecial);
