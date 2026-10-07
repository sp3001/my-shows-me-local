namespace Shows.Application.UseCases;

/// <summary>
/// One page of a Discovery bucket, filtered and paged (user-issues 19). Replaces the old
/// <c>GetUpcomingShowsUseCase</c>, which called the non-existent <c>/newshows</c> / <c>/upcoming</c>
/// endpoints (issue 1).
/// </summary>
/// <remarks>
/// <para>
/// The source does the heavy work — it queries the cached, indexed <c>/schedule/full</c> snapshot and
/// returns a single page already filtered and ordered. This use case adds only the one thing the source
/// cannot know: the already-tracked marker, which is a <c>user_show</c> fact (SPEC F2). It reads
/// <c>user_show</c> where <c>deleted_at IS NULL</c>, never <c>tracked_show</c>, so a soft-deleted show
/// reports untracked and can be re-added (ADR-024) — the same rule <see cref="SearchShowsUseCase"/>
/// enforces.
/// </para>
/// <para>
/// Stateless read (SPEC R8a): rendering a page writes nothing but, at most, a transport-cache entry when
/// the source refreshes its snapshot. Nothing here persists a show.
/// </para>
/// </remarks>
public sealed class GetDiscoveryPageUseCase(
    IDiscoverySource source,
    ITrackedShowRepository trackedShows)
{
    public async Task<DiscoveryPage> ExecuteAsync(
        DiscoveryBucket bucket,
        DiscoveryFilter filter,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        if (page < 1)
        {
            page = 1;
        }

        var result = await source
            .GetPageAsync(bucket, filter, page, pageSize, cancellationToken)
            .ConfigureAwait(false);

        if (result.Items.Count == 0)
        {
            return result;
        }

        // One batched lookup for the page's ids rather than one per row (SPEC F2 — reads user_show).
        var tracked = await trackedShows
            .GetActiveTrackingByTvmazeIdsAsync(
                [.. result.Items.Select(r => r.TvmazeId)], cancellationToken)
            .ConfigureAwait(false);

        var items = result.Items
            .Select(r =>
            {
                var trackedId = tracked.TryGetValue(r.TvmazeId, out var id) ? id : null;
                return r with { TrackedShowId = trackedId, IsAlreadyTracked = trackedId is not null };
            })
            .ToList();

        return result with { Items = items };
    }
}
