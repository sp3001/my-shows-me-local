namespace Shows.Application.UseCases;

/// <summary>
/// Reads one catalog show by its TVmaze id for the search-details view (user-issues 3 / improvement 10).
/// </summary>
/// <remarks>
/// <b>Why this exists next to <see cref="SearchShowsUseCase"/>.</b> A search result is not persisted
/// (SPEC P2/R8a), so a details view for an untracked show cannot go through
/// <see cref="GetShowDetailsUseCase"/>, which loads a <c>tracked_show</c> by surrogate id — the show has
/// no such id yet. This use case answers the only question the search-details page has: fetch the catalog
/// record and mark whether the user is already tracking it (against <c>user_show</c> where
/// <c>deleted_at IS NULL</c>, SPEC F2), so the page can offer add-to-category without a duplicate.
/// A stateless read (R8a): it writes nothing but a transport-cache entry. R14: an unreachable provider
/// throws from <see cref="ICatalogProvider"/> naming the host; the caller surfaces it, never swallows it.
/// </remarks>
public sealed class GetCatalogShowUseCase(
    ICatalogProvider provider,
    ITrackedShowRepository trackedShows)
{
    /// <summary>
    /// The catalog show with its already-tracked marker set, or <c>null</c> when the provider has no
    /// such show (a stale search hit) — the caller renders a friendly "not found" rather than crashing.
    /// </summary>
    public async Task<CatalogShow?> ExecuteAsync(int tvmazeId, CancellationToken cancellationToken)
    {
        var show = await provider.GetShowAsync(tvmazeId, cancellationToken).ConfigureAwait(false);
        if (show is null)
        {
            return null;
        }

        var tracked = await trackedShows
            .GetActiveTrackingByTvmazeIdsAsync([tvmazeId], cancellationToken)
            .ConfigureAwait(false);

        var trackedId = tracked.TryGetValue(tvmazeId, out var id) ? id : null;
        return show with { TrackedShowId = trackedId, IsAlreadyTracked = trackedId is not null };
    }
}
