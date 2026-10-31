using Shows.Domain;

namespace Shows.Application;

/// <summary>
/// Reads and writes the rebuildable show rows together with their 1:1 user row.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the only place the <c>deleted_at IS NULL</c> predicate lives</b> (SPEC R8,
/// ADR-024). The <c>List*</c> and <c>Find*</c> methods return actively-tracked shows only; the
/// <c>*IncludingUntracked</c> variants are the explicit escape hatch for the jobs and the
/// "previously tracked" view. It is deliberately not an EF global query filter, because the jobs must
/// be able to see soft-deleted rows and the choice has to stay visible at each call site
/// (<c>ARCHITECTURE.md</c> §5).
/// </para>
/// <para>
/// Every method returns a Domain type with <see cref="TrackedShow.User"/> populated, so no caller
/// reaches across two tables to learn a category. No method returns <c>IQueryable</c>
/// (<c>ARCHITECTURE.md</c> §3).
/// </para>
/// </remarks>
public interface ITrackedShowRepository
{
    Task<TrackedShow?> FindByIdAsync(long id, CancellationToken cancellationToken);

    /// <summary>
    /// The details-page read: the aggregate with its user row <b>and its genres</b> (SPEC F5). Separate
    /// from <see cref="FindByIdAsync"/> because that method is the hot path for the jobs and the recompute,
    /// where the genre join is pure cost; only the details page needs the genre names.
    /// </summary>
    Task<TrackedShow?> FindByIdWithGenresAsync(long id, CancellationToken cancellationToken);

    Task<TrackedShow?> FindByTvmazeIdAsync(int tvmazeId, CancellationToken cancellationToken);

    /// <summary>
    /// Finds by natural key <b>including</b> a soft-deleted show. The re-add path (step 3.2, ADR-024)
    /// must locate the existing <c>tracked_show</c> to restore its <c>user_show</c> rather than insert
    /// a second one — <see cref="FindByTvmazeIdAsync"/> filters <c>deleted_at IS NULL</c> and would
    /// return null, sending the caller down the new-show path into a UNIQUE <c>tvmaze_id</c> violation.
    /// </summary>
    Task<TrackedShow?> FindByTvmazeIdIncludingDeletedAsync(int tvmazeId, CancellationToken cancellationToken);

    /// <summary>Actively tracked shows only, ordered by title.</summary>
    Task<IReadOnlyList<TrackedShow>> ListActiveAsync(CancellationToken cancellationToken);

    /// <summary>
    /// The actively tracked shows among the given ids, ordered by title. The Updates tab's
    /// recently-watched section starts from <c>episode_watch</c> rows, which outlive untracking
    /// (ADR-024), so it needs an active-only lookup by id to drop a soft-deleted show's marks instead of
    /// listing a show the user removed.
    /// </summary>
    Task<IReadOnlyList<TrackedShow>> ListActiveByIdsAsync(
        IReadOnlyList<long> ids, CancellationToken cancellationToken);

    /// <summary>The category-tab query: <c>user_show</c> joined to <c>tracked_show</c>, active only (SPEC F4).</summary>
    Task<IReadOnlyList<TrackedShow>> ListActiveByCategoryAsync(
        ShowCategory category, CancellationToken cancellationToken);

    /// <summary>
    /// The job selection query (SPEC R8/R12): actively-tracked shows whose <c>status_id</c> is in the set.
    /// Selection is on the <b>enum</b>, never a raw string (R12), and always <c>deleted_at IS NULL</c> (R8) —
    /// a soft-deleted show keeps its <c>tracked_show</c> row, so a job that forgot the filter would burn
    /// external API calls on shows nobody tracks. This is the one shared selection the jobs use.
    /// </summary>
    Task<IReadOnlyList<TrackedShow>> ListActiveByStatusAsync(
        IReadOnlyCollection<ShowStatus> statuses, CancellationToken cancellationToken);

    /// <summary>
    /// The inverse of <see cref="ListActiveByStatusAsync"/>: actively-tracked shows whose <c>status_id</c>
    /// is NOT in the set. <c>EpisodeRefresh</c> uses this with <c>{Ended, Canceled}</c> so it refreshes
    /// airing, unknown, and everything that failed to map — erring toward refreshing more (step 7.1).
    /// </summary>
    Task<IReadOnlyList<TrackedShow>> ListActiveExcludingStatusAsync(
        IReadOnlyCollection<ShowStatus> statuses, CancellationToken cancellationToken);

    /// <summary>
    /// Includes soft-deleted shows. For the job selection queries and the "previously tracked" view —
    /// never for a tab.
    /// </summary>
    Task<IReadOnlyList<TrackedShow>> ListIncludingUntrackedAsync(CancellationToken cancellationToken);

    /// <summary>
    /// The "already tracked" marker in Search and Discovery. Reads <c>user_show</c> filtered
    /// <c>deleted_at IS NULL</c>, <b>not</b> <c>tracked_show</c> (SPEC F2): a soft-deleted show still
    /// has a <c>tracked_show</c> row, so checking that table would report it as tracked and block the
    /// very re-add the design is built around.
    /// <para>
    /// The value is the active <c>tracked_show</c> id when tracked, or <c>null</c> when not — so a caller
    /// learns both <i>whether</i> a catalog hit is tracked (a non-null value) and <i>which</i> tracked row
    /// it is, letting a tracked card link straight to its tracked-details page rather than the catalog one.
    /// </para>
    /// </summary>
    Task<Dictionary<int, long?>> GetActiveTrackingByTvmazeIdsAsync(
        IReadOnlyList<int> tvmazeIds, CancellationToken cancellationToken);

    /// <summary>
    /// The seasons of one show, in season-number order — the light read a list card needs to render its
    /// season headers without loading genres, mismatches, or every episode (issue 7). The show id is the
    /// caller's already-active <c>tracked_show</c> id, so no <c>deleted_at</c> filter is re-applied here;
    /// a card only exists for an active show.
    /// </summary>
    Task<IReadOnlyList<Season>> ListSeasonsByShowIdAsync(long showId, CancellationToken cancellationToken);

    /// <summary>
    /// The seasons of many shows at once, for the batched Started-list read. Each <see cref="Season"/>
    /// carries its <see cref="Season.ShowId"/>, so the caller groups the flat result by show. Same light
    /// read as <see cref="ListSeasonsByShowIdAsync"/> — season rows only, no genres or episodes.
    /// </summary>
    Task<IReadOnlyList<Season>> ListSeasonsByShowIdsAsync(
        IReadOnlyList<long> showIds, CancellationToken cancellationToken);

    Task AddAsync(TrackedShow show, CancellationToken cancellationToken);
}
