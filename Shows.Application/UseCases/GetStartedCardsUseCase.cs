using Shows.Domain;

namespace Shows.Application.UseCases;

/// <summary>
/// The batched card read for the <b>Started</b> list: the seasons + expansion + filtered inline rows for
/// <em>every</em> card in one pass, returned keyed by tracked-show id so each card can build synchronously.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists (Started list performance).</b> <see cref="GetCardSeasonsUseCase.ExecuteAsync"/> is the
/// right read for a single card, but the Started list realizes all its cards at once (ADR-032: it is the one
/// category that expands inline, so it uses a re-measuring layout rather than a virtualized
/// <c>CollectionView</c>). Driving one <see cref="GetCardSeasonsUseCase"/> per card means ~5 queries per card
/// — for 78 shows that is ~390 round-trips, each opening its own DI scope, which is what made switching back
/// to Started take ~3 s. This use case issues the same reads as <b>five</b> set-based queries
/// (<c>WHERE show_id IN (...)</c>) and slices the result per show, so the data cost is constant regardless of
/// list length.
/// </para>
/// <para>
/// <b>Always fresh, never cached.</b> This is not a cache that skips reads — it reads the database every time
/// the tab loads, so a background job's changes are reflected on the next load with no invalidation logic. The
/// optimization is purely batching the reads, not avoiding them.
/// </para>
/// <para>
/// The per-show result is the same <see cref="CardSeasons"/> record the single-card read returns, so the card
/// builds its season groups identically whether the data arrived batched or per-card. Collapsed seasons still
/// load their rows lazily on first expand via <see cref="GetCardSeasonsUseCase.ExecuteSeasonAsync"/> — only
/// the eager (expanded-season) rows are batched here, matching the single-card contract (issue 7).
/// </para>
/// </remarks>
public sealed class GetStartedCardsUseCase(
    ITrackedShowRepository trackedShows,
    IEpisodeRepository episodes,
    IUiStateRepository uiState)
{
    /// <summary>
    /// Loads the card read model for every show in <paramref name="trackedShowIds"/> in a handful of
    /// set-based queries. The returned <see cref="StartedCardsBatch.ByShow"/> has an entry for every
    /// requested id (an empty <see cref="CardSeasons"/> for a show with no seasons), so the caller never
    /// has to special-case a miss. <see cref="StartedCardsBatch.LastAiredByShow"/> carries each show's most
    /// recent already-aired episode date for the Started sort (improvement 12); a show with none is absent.
    /// </summary>
    public async Task<StartedCardsBatch> ExecuteAsync(
        IReadOnlyList<long> trackedShowIds, DateOnly today, CancellationToken cancellationToken)
    {
        var result = new Dictionary<long, CardSeasons>(trackedShowIds.Count);
        if (trackedShowIds.Count == 0)
        {
            return new StartedCardsBatch(result, new Dictionary<long, DateOnly>());
        }

        // 1. Every season of every show, grouped by show.
        var seasonsByShow = (await trackedShows
            .ListSeasonsByShowIdsAsync(trackedShowIds, cancellationToken).ConfigureAwait(false))
            .GroupBy(s => s.ShowId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<Season>)g.ToList());

        // 2. Every persisted-expanded season id, grouped by show. Kept un-intersected with the season list
        //    (matching the single-card read): a stale id is harmless because the card only tests membership
        //    while iterating its own seasons.
        var expandedByShow = (await uiState
            .ListByShowIdsAsync(trackedShowIds, cancellationToken).ConfigureAwait(false))
            .Where(u => u.IsExpanded)
            .GroupBy(u => u.ShowId)
            .ToDictionary(g => g.Key, g => g.Select(u => u.SeasonId).ToHashSet());

        // 3. One watched-ids read for all shows. Episode ids are globally unique (SPEC R13), so a single
        //    set is correct for the listable filter across every show.
        var watchedIds = (await episodes
            .ListWatchesByShowIdsAsync(trackedShowIds, cancellationToken).ConfigureAwait(false))
            .Select(w => w.EpisodeId)
            .ToHashSet();

        // 4. One episode read for every expanded season across all shows, filtered to the listable rows and
        //    grouped by season. Collapsed seasons are absent here by design — their rows load lazily.
        var allExpandedSeasonIds = expandedByShow.Values.SelectMany(ids => ids).Distinct().ToList();
        var expandedEpisodes = allExpandedSeasonIds.Count > 0
            ? await episodes.ListBySeasonIdsAsync(allExpandedSeasonIds, cancellationToken).ConfigureAwait(false)
            : [];
        var listableBySeason = expandedEpisodes
            .Where(e => InlineEpisodeRules.IsListable(e, watchedIds.Contains(e.Id), today))
            .GroupBy(e => e.SeasonId)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<Episode>)g.OrderBy(e => e, SeasonEpisodeOrdering.EpisodesDescending).ToList());

        // 5. One grouped count for every season of every show (keyed by season id, which is globally unique).
        var counts = await episodes
            .CountDisplayableEpisodesBySeasonForShowsAsync(trackedShowIds, today, cancellationToken)
            .ConfigureAwait(false);

        // 5b. The matching per-season runtime total, same predicate and key — the season header's duration.
        var runtimeBySeason = await episodes
            .SumRuntimeBySeasonForShowsAsync(trackedShowIds, today, cancellationToken)
            .ConfigureAwait(false);

        // 6. The most recent already-aired date per show, for the Started "sort by last aired" (improvement
        //    12). Computed here rather than cached on tracked_show (SPEC P5) — the batch read is the one
        //    place that already touches every Started show, so the sort needs no schema column.
        var lastAired = await episodes
            .LastAiredByShowIdsAsync(trackedShowIds, today, cancellationToken)
            .ConfigureAwait(false);

        // Slice the batched data back into a per-show CardSeasons identical to the single-card read.
        foreach (var showId in trackedShowIds)
        {
            var seasons = seasonsByShow.TryGetValue(showId, out var s) ? s : [];
            var expandedIds = expandedByShow.TryGetValue(showId, out var ex) ? ex : new HashSet<long>();

            var bySeason = new Dictionary<long, IReadOnlyList<Episode>>();
            var showCounts = new Dictionary<long, int>();
            var showRuntime = new Dictionary<long, int>();
            foreach (var season in seasons)
            {
                if (expandedIds.Contains(season.Id)
                    && listableBySeason.TryGetValue(season.Id, out var rows))
                {
                    bySeason[season.Id] = rows;
                }

                if (counts.TryGetValue(season.Id, out var count))
                {
                    showCounts[season.Id] = count;
                }

                if (runtimeBySeason.TryGetValue(season.Id, out var minutes))
                {
                    showRuntime[season.Id] = minutes;
                }
            }

            result[showId] = new CardSeasons(seasons, expandedIds, bySeason, showCounts, showRuntime);
        }

        return new StartedCardsBatch(result, lastAired);
    }
}

/// <summary>
/// The result of a batched Started-list read: the per-show card data plus the per-show last-aired date the
/// Started sort needs (improvement 12). Returned together so the list sorts without a second query.
/// </summary>
/// <param name="ByShow">The card read model per tracked-show id, identical to the single-card read.</param>
/// <param name="LastAiredByShow">Each show's most recent already-aired episode date; a show with none is absent.</param>
public sealed record StartedCardsBatch(
    IReadOnlyDictionary<long, CardSeasons> ByShow,
    IReadOnlyDictionary<long, DateOnly> LastAiredByShow);
