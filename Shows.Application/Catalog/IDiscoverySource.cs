namespace Shows.Application;

/// <summary>
/// The Discovery read seam (user-issues 19). One implementation today: the parsed TVmaze
/// <c>/schedule/full</c> snapshot held in an in-memory cache (Infrastructure).
/// </summary>
/// <remarks>
/// <para>
/// This is deliberately a <b>separate</b> seam from <see cref="ICatalogProvider"/>. The provider is a
/// live per-request HTTP read; Discovery is a query over a locally cached, indexed snapshot that is
/// refreshed at most once a day. Folding the two would imply Discovery hits the network per keystroke,
/// which is exactly the behaviour issue 19 is replacing.
/// </para>
/// <para>
/// <b>R14 still applies.</b> A cold cache that cannot be filled (the schedule host is unreachable and
/// no prior snapshot exists) must throw naming the host, never return an empty page — an empty page
/// would render as "there are no new shows", which is false and indistinguishable from success.
/// </para>
/// </remarks>
public interface IDiscoverySource
{
    /// <summary>
    /// Return one page of the given bucket after applying <paramref name="filter"/>. The source owns
    /// filtering, ordering and paging; the caller never sees the whole schedule.
    /// </summary>
    Task<DiscoveryPage> GetPageAsync(
        DiscoveryBucket bucket,
        DiscoveryFilter filter,
        int page,
        int pageSize,
        CancellationToken cancellationToken);

    /// <summary>
    /// The cached snapshot's entry for a show, if it is present — its <see cref="CatalogShow"/> plus the
    /// scheduled episodes (season / number / air date / title). Used by the Discovery details page so it
    /// renders entirely from the already-loaded snapshot with <b>no network call</b> (user-issues 19
    /// follow-up: the details page must not re-fetch, which is what made it hang on the unreachable host).
    /// </summary>
    /// <remarks>
    /// A pure cache read: returns <c>false</c> when the show is not in the snapshot (a Discovery hit always
    /// is, since it came from this source; a Search hit never is). It never fetches and never throws — the
    /// caller falls back to a live read when this returns false.
    /// </remarks>
    bool TryGetCached(int tvmazeId, out CatalogShow show, out IReadOnlyList<CatalogEpisode> episodes);
}
