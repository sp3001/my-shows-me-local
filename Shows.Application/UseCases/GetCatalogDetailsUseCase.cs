namespace Shows.Application.UseCases;

/// <summary>
/// The details for an <b>untracked</b> catalog show — the show plus its episode list (user-issues 19
/// follow-up). Replaces the bare <see cref="GetCatalogShowUseCase"/> read behind the details page so the
/// page can show episodes too.
/// </summary>
/// <remarks>
/// <para>
/// <b>Show metadata is cache-first; episodes are always live.</b> A Discovery hit's show card is served
/// from the cached <c>/schedule/full</c> snapshot (no <see cref="ICatalogProvider.GetShowAsync"/> call),
/// so the page opens instantly. The episode list, however, is always fetched with
/// <see cref="ICatalogProvider.GetEpisodesAsync"/> — the snapshot only carries scheduled/airing rows, so
/// serving a Discovery show's episodes from it would list a handful of future episodes instead of the
/// complete guide. A Search hit (not in the snapshot) reads the show live too.
/// </para>
/// <para>
/// <b>R14.</b> The live episode fetch throws naming the host when TVmaze is unreachable — never an empty
/// page. The cached metadata path cannot fail that way, but the episode fetch can, so the page surfaces
/// the host-naming message rather than a partial list.
/// </para>
/// <para>
/// Stateless read (SPEC R8a): writes nothing but, at most, a transport-cache entry on the live path. The
/// already-tracked marker is a <c>user_show</c> fact (SPEC F2), read where <c>deleted_at IS NULL</c>.
/// </para>
/// </remarks>
public sealed class GetCatalogDetailsUseCase(
    ICatalogProvider provider,
    IDiscoverySource discovery,
    ITrackedShowRepository trackedShows)
{
    /// <summary>
    /// The show with its episodes and already-tracked marker, or <c>null</c> when the provider has no such
    /// show (a stale hit) — the caller renders a friendly "not found" rather than crashing.
    /// </summary>
    public async Task<CatalogDetails?> ExecuteAsync(int tvmazeId, CancellationToken cancellationToken)
    {
        // Show metadata: from the cached schedule snapshot when present (a Discovery hit), else live.
        CatalogShow show;
        if (discovery.TryGetCached(tvmazeId, out var cachedShow, out _))
        {
            show = cachedShow;
        }
        else
        {
            var fetched = await provider.GetShowAsync(tvmazeId, cancellationToken).ConfigureAwait(false);
            if (fetched is null)
            {
                return null;
            }

            show = fetched;
        }

        // Episodes: always fetched live for the full guide — the snapshot only holds scheduled rows, so a
        // Discovery show would otherwise list only its upcoming episodes (user request: fetch like Search).
        var raw = await provider.GetEpisodesAsync(tvmazeId, cancellationToken).ConfigureAwait(false);
        var episodes = raw.Select(MapEpisode).ToList();

        var tracked = await trackedShows
            .GetActiveTrackingByTvmazeIdsAsync([tvmazeId], cancellationToken)
            .ConfigureAwait(false);

        var trackedId = tracked.TryGetValue(tvmazeId, out var id) ? id : null;
        show = show with { TrackedShowId = trackedId, IsAlreadyTracked = trackedId is not null };

        return new CatalogDetails(show, GroupBySeason(episodes));
    }

    private static CatalogEpisode MapEpisode(EpisodeCatalogInfo ep) => new(
        ep.SeasonNumber,
        ep.EpisodeNumber,
        ep.Title,
        DateOnly.TryParse(ep.AirDateRaw, null, System.Globalization.DateTimeStyles.None, out var d) ? d : null,
        // The untracked page has no progress semantics, so it collapses every special kind to one
        // marker (ADR-034): a season-0 row or any TVmaze special type (significant or insignificant) is
        // flagged. The tracked page is where the significant/insignificant split matters.
        IsSpecial: ep.SeasonNumber == 0
            || ep.TypeRaw is "special" or "significant_special" or "insignificant_special");

    // Group into seasons, DESCENDING by season number (newest season first). Within a season, episodes sort
    // by air date descending, then episode number descending — newest episode first. Nulls sort last within
    // each tier (an undated or unnumbered episode falls to the bottom of its season) via the
    // "has value ? value : sentinel" trick, since OrderByDescending would otherwise put nulls first.
    private static List<CatalogSeason> GroupBySeason(IReadOnlyList<CatalogEpisode> episodes) =>
        episodes
            .GroupBy(e => e.SeasonNumber)
            .OrderByDescending(g => g.Key)
            .Select(g => new CatalogSeason(
                g.Key,
                g.OrderByDescending(e => e.AirDate ?? DateOnly.MinValue)
                    .ThenByDescending(e => e.Number ?? int.MinValue)
                    .ToList()))
            .ToList();
}
