using Shows.Domain;

namespace Shows.Application.UseCases;

/// <summary>
/// The light read model a My Shows list card needs to render its season rows immediately (no card-level
/// "Episodes" toggle): the season list, which seasons are persisted-expanded (R9), and — for the expanded
/// seasons only — the filtered inline episode rows.
/// </summary>
/// <remarks>
/// Deliberately lighter than <see cref="ShowDetails"/>: no genres, no mismatches, and <b>no episode rows
/// for collapsed seasons</b> (those load lazily on first expand via
/// <see cref="GetCardSeasonsUseCase.ExecuteSeasonAsync"/>). A category tab can hold hundreds of cards, so
/// the card path must not pay for the full details read per card (issue 7).
/// </remarks>
/// <param name="Seasons">Every season of the show, in season-number order (specials season included; the
/// card decides whether it renders it).</param>
/// <param name="ExpandedSeasonIds">The season ids the user has persisted-expanded (SPEC R9).</param>
/// <param name="EpisodesByExpandedSeason">For each expanded season, its already-filtered inline rows
/// (unwatched, already-aired regulars — <see cref="InlineEpisodeRules"/>), newest-first. Collapsed seasons
/// are absent here by design; their rows are not materialized at load time.</param>
/// <param name="DisplayableCountBySeason">Per-season count of displayable episodes (the same predicate as
/// <see cref="InlineEpisodeRules.IsListable"/>), computed eagerly for <b>every</b> season via a cheap
/// grouped aggregate. The card uses it to drop zero-count seasons before they are ever shown and to render
/// the header's "N left" text (issue 11) — without loading a collapsed season's rows.</param>
/// <param name="RuntimeMinutesBySeason">Per-season total runtime (minutes) of the <b>displayable</b>
/// episodes, so the season header can show how long what is left actually takes. Sparse: a season whose
/// episodes carry no runtime is absent, and the header shows no duration. Computed, never stored (SPEC P5).</param>
public sealed record CardSeasons(
    IReadOnlyList<Season> Seasons,
    IReadOnlySet<long> ExpandedSeasonIds,
    IReadOnlyDictionary<long, IReadOnlyList<Episode>> EpisodesByExpandedSeason,
    IReadOnlyDictionary<long, int> DisplayableCountBySeason,
    IReadOnlyDictionary<long, int> RuntimeMinutesBySeason);

/// <summary>
/// Reads a card's seasons + expanded state + the filtered inline rows for the expanded seasons, and loads
/// a single collapsed season's filtered rows on demand. The card's inline list is filtered to
/// unwatched-and-already-aired regulars (improvement 4); the details page is not this read model and shows
/// everything (improvement 11).
/// </summary>
/// <remarks>
/// <b>Why a separate read model (issue 7).</b> <see cref="GetShowDetailsUseCase"/> loads every episode,
/// the genres, and the mismatches — fine for one details page, wasteful per card across a long list. This
/// use case loads only the seasons and the expanded-season episodes, so a card with every season collapsed
/// loads zero episode rows. "Today" is a parameter (not read from the clock here) so the filter is pure and
/// testable; the caller passes the UTC date.
/// </remarks>
public sealed class GetCardSeasonsUseCase(
    ITrackedShowRepository trackedShows,
    IEpisodeRepository episodes,
    IUiStateRepository uiState)
{
    /// <summary>
    /// The card's initial load: seasons + expanded ids + filtered rows for the expanded seasons only.
    /// </summary>
    public async Task<CardSeasons> ExecuteAsync(
        long trackedShowId, DateOnly today, CancellationToken cancellationToken)
    {
        var seasons = await trackedShows
            .ListSeasonsByShowIdAsync(trackedShowId, cancellationToken).ConfigureAwait(false);
        var states = await uiState
            .ListByShowIdAsync(trackedShowId, cancellationToken).ConfigureAwait(false);

        var expandedIds = states
            .Where(s => s.IsExpanded)
            .Select(s => s.SeasonId)
            .ToHashSet();

        // Only expanded seasons need rows now; collapsed ones load lazily when the user expands them.
        var expandedSeasonIds = seasons
            .Where(s => expandedIds.Contains(s.Id))
            .Select(s => s.Id)
            .ToList();

        var bySeason = new Dictionary<long, IReadOnlyList<Episode>>();
        if (expandedSeasonIds.Count > 0)
        {
            var watched = await FilterForList(trackedShowId, expandedSeasonIds, today, cancellationToken)
                .ConfigureAwait(false);
            foreach (var group in watched.GroupBy(e => e.SeasonId))
            {
                bySeason[group.Key] = group
                    .OrderBy(e => e, SeasonEpisodeOrdering.EpisodesDescending)
                    .ToList();
            }
        }

        // issue 11: the eager per-season displayable count, computed for every season in one grouped
        // aggregate so the card can drop empty seasons before expanding them and show "N left" in the
        // header — without materializing a collapsed season's rows (which the lazy path, issue 7, forbids).
        var counts = await episodes
            .CountDisplayableEpisodesBySeasonAsync(trackedShowId, today, cancellationToken)
            .ConfigureAwait(false);

        // The season header's total duration, over the same displayable set as the count above so the two
        // numbers describe the same episodes.
        var runtime = await episodes
            .SumRuntimeBySeasonAsync(trackedShowId, today, cancellationToken)
            .ConfigureAwait(false);

        return new CardSeasons(seasons, expandedIds, bySeason, counts, runtime);
    }

    /// <summary>
    /// The lazy per-season load: a collapsed season's filtered inline rows, materialized only when the user
    /// first expands it. Newest-first, same filter and order as the eager path.
    /// </summary>
    public async Task<IReadOnlyList<Episode>> ExecuteSeasonAsync(
        long trackedShowId, long seasonId, DateOnly today, CancellationToken cancellationToken)
    {
        var filtered = await FilterForList(trackedShowId, [seasonId], today, cancellationToken)
            .ConfigureAwait(false);
        return filtered
            .OrderBy(e => e, SeasonEpisodeOrdering.EpisodesDescending)
            .ToList();
    }

    // Load the episodes of the given seasons and keep only the listable ones (unwatched, already-aired
    // regulars). One watched-ids read + one season-scoped episode read; collapsed seasons never reach here.
    private async Task<IReadOnlyList<Episode>> FilterForList(
        long trackedShowId, IReadOnlyList<long> seasonIds, DateOnly today, CancellationToken cancellationToken)
    {
        var watches = await episodes
            .ListWatchesByShowIdAsync(trackedShowId, cancellationToken).ConfigureAwait(false);
        var watchedIds = watches.Select(w => w.EpisodeId).ToHashSet();

        var seasonEpisodes = await episodes
            .ListBySeasonIdsAsync(seasonIds, cancellationToken).ConfigureAwait(false);

        return seasonEpisodes
            .Where(e => InlineEpisodeRules.IsListable(e, watchedIds.Contains(e.Id), today))
            .ToList();
    }
}
