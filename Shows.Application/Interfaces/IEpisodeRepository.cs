using Shows.Domain;

namespace Shows.Application;

/// <summary>
/// Reads episodes and persists the user's watched marks.
/// </summary>
/// <remarks>
/// <para>
/// The watch-mark methods live here rather than on a separate interface because a watched mark is
/// meaningless without the episode it points at, and <c>episode_watch.episode_id</c> references a
/// surrogate id that is permanent for the life of the row (SPEC R13). Keeping them together makes it
/// impossible to fetch episodes through one seam and mutate watches through another without noticing.
/// </para>
/// <para>
/// <b>Deleting a watch row is the one user action that is a real delete</b> — absence is the meaning
/// (<c>SCHEMA.md</c> §2 <c>episode_watch</c>). It is not a soft delete, and unlike untracking it does
/// not violate SPEC P4.
/// </para>
/// </remarks>
public interface IEpisodeRepository
{
    /// <summary>All episodes of a show in canonical order (<c>air_stamp</c>, then season/number).</summary>
    Task<IReadOnlyList<Episode>> ListByShowIdAsync(long showId, CancellationToken cancellationToken);

    Task<Episode?> FindByIdAsync(long id, CancellationToken cancellationToken);

    Task AddRangeAsync(IReadOnlyList<Episode> episodes, CancellationToken cancellationToken);

    /// <summary>Existing watch rows for a show, keyed by episode id.</summary>
    Task<IReadOnlyList<EpisodeWatch>> ListWatchesByShowIdAsync(
        long showId, CancellationToken cancellationToken);

    /// <summary>
    /// Existing watch rows for many shows at once, for the batched Started-list read. Episode ids are
    /// globally unique (SPEC R13), so the caller collapses the flat result into one watched-id set.
    /// </summary>
    Task<IReadOnlyList<EpisodeWatch>> ListWatchesByShowIdsAsync(
        IReadOnlyList<long> showIds, CancellationToken cancellationToken);

    /// <summary>
    /// The episodes belonging to a set of seasons, for the card's lazy per-season load. Scoped to the
    /// given season ids so a card with most seasons collapsed never materializes the whole episode list
    /// (issue 7). Ordering is left to the caller (<see cref="SeasonEpisodeOrdering"/>), matching
    /// <see cref="ListByShowIdAsync"/>, which also orders in-process because SQLite cannot order a
    /// <c>DateTimeOffset</c>.
    /// </summary>
    Task<IReadOnlyList<Episode>> ListBySeasonIdsAsync(
        IReadOnlyList<long> seasonIds, CancellationToken cancellationToken);

    /// <summary>
    /// The show's recorded TVmaze/IMDb disagreements (step 7.3, ADR-031) for the details page. Read-only:
    /// the enrichment mapper is the sole writer, the UI cannot read the <c>imdb_*</c> source tables, so the
    /// materialised <c>episode_mismatch</c> rows are what the page renders.
    /// </summary>
    Task<IReadOnlyList<EpisodeMismatch>> ListMismatchesByShowIdAsync(
        long showId, CancellationToken cancellationToken);

    /// <summary>
    /// The single-table count that backs progress. Denormalised <c>show_id</c> on
    /// <c>episode_watch</c> is what makes this cheap (<c>SCHEMA.md</c> §2).
    /// </summary>
    Task<int> CountWatchesByShowIdAsync(long showId, CancellationToken cancellationToken);

    /// <summary>
    /// Per-season count of <b>displayable</b> episodes — the ones a My Shows card would list: not a
    /// special, unwatched, and already aired on or before <paramref name="today"/> (the same predicate as
    /// <see cref="InlineEpisodeRules.IsListable"/>). Returned as a sparse map (a season with nothing to
    /// show is absent, i.e. counts as zero) so the card can drop empty seasons <em>before</em> expanding
    /// them and show the count in the header (issue 11) without materializing every episode row.
    /// </summary>
    /// <remarks>
    /// A grouped aggregate, not a row load: a category tab can hold hundreds of cards and the lazy design
    /// (issue 7) forbids loading all of a show's episodes per card. The count is computed in SQL so only
    /// <c>(season_id, count)</c> pairs cross the wire.
    /// </remarks>
    Task<IReadOnlyDictionary<long, int>> CountDisplayableEpisodesBySeasonAsync(
        long showId, DateOnly today, CancellationToken cancellationToken);

    /// <summary>
    /// The same per-season displayable count as
    /// <see cref="CountDisplayableEpisodesBySeasonAsync"/>, but for many shows in one grouped aggregate. The
    /// result is keyed by <b>season id</b> (globally unique), so the caller slices it per show. Same predicate
    /// as <see cref="InlineEpisodeRules.IsListable"/>.
    /// </summary>
    Task<IReadOnlyDictionary<long, int>> CountDisplayableEpisodesBySeasonForShowsAsync(
        IReadOnlyList<long> showIds, DateOnly today, CancellationToken cancellationToken);

    /// <summary>
    /// The most recent <b>already-aired</b> episode date per show (improvement 12's "sort by last aired"),
    /// for many shows in one grouped query. A show with no aired episode is absent (sorts last). Computed
    /// rather than cached (SPEC P5) — the Started batch read supplies it so the sort needs no schema column.
    /// </summary>
    Task<IReadOnlyDictionary<long, DateOnly>> LastAiredByShowIdsAsync(
        IReadOnlyList<long> showIds, DateOnly today, CancellationToken cancellationToken);

    /// <summary>
    /// Per-season <b>total runtime in minutes</b> of the <em>displayable</em> episodes (the same predicate
    /// as <see cref="InlineEpisodeRules.IsListable"/>), for one show. Keyed by <b>season id</b>. A season
    /// whose episodes have no runtime at all is absent, so the header shows no duration rather than
    /// "0 min". Computed, never stored (SPEC P5) — <c>episode.runtime_minutes</c> is per episode and the
    /// card's season line is the only place the sum is needed.
    /// </summary>
    Task<IReadOnlyDictionary<long, int>> SumRuntimeBySeasonAsync(
        long showId, DateOnly today, CancellationToken cancellationToken);

    /// <summary>
    /// The same per-season runtime total as <see cref="SumRuntimeBySeasonAsync"/>, for many shows in one
    /// grouped query. Keyed by <b>season id</b> (globally unique), so the caller slices it per show.
    /// </summary>
    Task<IReadOnlyDictionary<long, int>> SumRuntimeBySeasonForShowsAsync(
        IReadOnlyList<long> showIds, DateOnly today, CancellationToken cancellationToken);

    /// <summary>
    /// The <b>upcoming</b> regular episodes of many shows at once (improvement 4's upcoming view): a
    /// regular whose air date is unknown (null) or strictly after <paramref name="today"/>. Specials are
    /// excluded (SPEC R4 — they live only on the details page). Ordering is left to the caller.
    /// </summary>
    Task<IReadOnlyList<Episode>> ListUpcomingByShowIdsAsync(
        IReadOnlyList<long> showIds, DateOnly today, CancellationToken cancellationToken);

    /// <summary>
    /// The episodes with the given surrogate ids, for the Updates tab's recently-watched section — the
    /// watch rows name the episode ids, and the row needs its show/season/number/title. Ordering is left
    /// to the caller, matching <see cref="ListByShowIdAsync"/>.
    /// </summary>
    Task<IReadOnlyList<Episode>> ListByIdsAsync(
        IReadOnlyList<long> episodeIds, CancellationToken cancellationToken);

    /// <summary>
    /// Watch rows marked on or after <paramref name="since"/>, newest first, for the Updates tab's
    /// recently-watched section. A soft-deleted show's marks still come back here — the caller resolves
    /// shows through an active-only read, so an untracked show's rows drop out rather than leaking.
    /// </summary>
    /// <remarks>
    /// <b>Raw SQL, deliberately.</b> EF Core on SQLite cannot translate a <c>DateTimeOffset</c>
    /// comparison in a LINQ <c>WHERE</c> at all (the standing constraint in this project), and filtering
    /// 10k+ watch rows in memory on every tab open is the one read here that is not already bounded to a
    /// few hundred rows. <c>watched_at</c> is stored UTC-normalized with a uniform <c>+00:00</c> offset,
    /// so the TEXT comparison and the TEXT ordering are both chronological.
    /// </remarks>
    Task<IReadOnlyList<EpisodeWatch>> ListWatchesSinceAsync(
        DateTimeOffset since, CancellationToken cancellationToken);

    Task AddWatchAsync(EpisodeWatch watch, CancellationToken cancellationToken);

    /// <summary>Returns whether a row was actually removed, so a double-unwatch is not silently a no-op success.</summary>
    Task<bool> RemoveWatchAsync(long episodeId, CancellationToken cancellationToken);
}
