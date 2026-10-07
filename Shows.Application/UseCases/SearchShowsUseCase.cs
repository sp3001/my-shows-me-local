namespace Shows.Application.UseCases;

/// <summary>
/// Debounced name search against the catalog provider (SPEC F2).
/// </summary>
/// <remarks>
/// <b>Stateless read</b> (SPEC R8a): rendering results writes nothing except a transport-cache entry.
/// There is no "import into local catalog" step and no deduplication against a catalog table —
/// deduplication is against <c>user_show</c> where <c>deleted_at IS NULL</c>, so a soft-deleted show is
/// correctly shown as not tracked and can be re-added.
/// </remarks>
public sealed class SearchShowsUseCase(
    ICatalogProvider provider,
    ITrackedShowRepository trackedShows)
{
    public async Task<IReadOnlyList<CatalogShow>> ExecuteAsync(
        string query, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);

        var results = await provider.SearchAsync(query, cancellationToken).ConfigureAwait(false);

        if (results.Count == 0)
        {
            return results;
        }

        // One batched lookup rather than one per row. Reads user_show, not tracked_show (SPEC F2).
        var tracked = await trackedShows
            .GetActiveTrackingByTvmazeIdsAsync(
                [.. results.Select(r => r.TvmazeId)], cancellationToken)
            .ConfigureAwait(false);

        return results
            .Select(r =>
            {
                var trackedId = tracked.TryGetValue(r.TvmazeId, out var id) ? id : null;
                return r with { TrackedShowId = trackedId, IsAlreadyTracked = trackedId is not null };
            })
            .ToList();
    }
}
