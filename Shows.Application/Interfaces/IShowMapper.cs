namespace Shows.Application;

/// <summary>
/// The single enrichment mapper — the only code path that writes mapped rows (SPEC R11, ADR-023).
/// </summary>
/// <remarks>
/// <para>
/// Adding a show is "Enrichment scoped to one show": the caller creates the <c>tracked_show</c> and
/// <c>user_show</c> rows, then calls this. <c>--promote</c>, <c>AddShowToTracking</c> and all four
/// jobs share this one implementation, which is the difference between "four jobs that each have their
/// own idea of how to fill <c>tracked_show</c>" and "four fetchers plus one mapper".
/// </para>
/// <para>
/// The hard constraints are enforced by tests, not comments (step 1.5): it never writes
/// <c>user_show</c> / <c>episode_watch</c> / <c>ui_season_state</c> (SPEC P4); it never regenerates a
/// surrogate id, because <c>episode_watch.episode_id</c> points at them and delete-then-insert would
/// silently orphan the user's watch marks while every row count still looked correct; and it is
/// idempotent.
/// </para>
/// </remarks>
public interface IShowMapper
{
    /// <summary>
    /// Fetches the payload if needed, writes the source rows verbatim, then upserts the mapped rows.
    /// </summary>
    /// <remarks>
    /// The id is TVmaze's, not a surrogate: the mapper is addressed by the natural key because that
    /// is what it upserts on.
    /// </remarks>
    Task<MapResult> MapShowAsync(int tvmazeId, CancellationToken cancellationToken);

    /// <summary>
    /// Ensures a <c>tvmaze_show</c> source row exists for the id, creating a minimal shell if none is
    /// present, so a caller can insert a <c>tracked_show</c> whose NOT NULL <c>tvmaze_show_id</c> FK
    /// points at it (SCHEMA §2). The mapper is the <b>sole</b> writer of source rows (SPEC R11), so the
    /// shell is minted here rather than by the repository or the use case; <see cref="MapShowAsync"/>
    /// overwrites it with the real payload on the first map. No-op when the row already exists (a re-add
    /// of a soft-deleted show, or a show already enriched). Writes nothing to the mapped or user tables.
    /// </summary>
    Task EnsureSourceRowAsync(int tvmazeId, CancellationToken cancellationToken);

    /// <summary>
    /// Write a show's <c>/akas</c> source rows verbatim and map the target-country aka into
    /// <c>tracked_show.title_localized</c> (step 7.5). The pick is the lowest-order aka whose country code
    /// equals <paramref name="targetCountry"/>; no match leaves the localized title null (never a
    /// wrong-country guess). The target country is passed in by the caller (configuration), not hard-coded
    /// here. Returns whether the mapped title changed and the picked title, so the <c>--akas</c> job can
    /// count <c>items_changed</c> and the details-page button can list the alternates.
    /// </summary>
    Task<AkaMapResult> MapAkasAsync(
        int tvmazeId, IReadOnlyList<ShowAka> akas, string targetCountry, CancellationToken cancellationToken);
}

/// <summary>
/// What one <c>/akas</c> map did (step 7.5). <see cref="Changed"/> is the mapped-title change — the signal
/// the job counts as <c>items_changed</c>; source-row churn is not a change. <see cref="PickedTitle"/> is
/// the title now in <c>title_localized</c> (null when no aka matched the target country).
/// </summary>
public sealed record AkaMapResult(bool Changed, string? PickedTitle)
{
    public static AkaMapResult Unchanged { get; } = new(false, null);
}

/// <summary>
/// What one <c>Enrichment --imdb</c> map did (step 7.3, ADR-031). <see cref="Changed"/> is the episode
/// field-fill signal the job counts as <c>items_changed</c> — a gap filled or the imdb link set. The
/// mismatch rows are wiped and recomputed every run, so <see cref="Mismatches"/> is the count of
/// disagreements now recorded for the show, not a change count (re-running reports the same number with
/// <see cref="Changed"/> false).
/// </summary>
public sealed record EnrichResult(bool Changed, int Mismatches)
{
    public static EnrichResult Unchanged { get; } = new(false, 0);
}

/// <summary>
/// What a single map operation changed, so callers can report <c>job_run.items_changed</c> honestly.
/// </summary>
/// <remarks>
/// <c>items_changed</c> is measured <b>after</b> the mapper, not after the fetch (step 7.x): a job that
/// fetched 547 shows and changed nothing must report 0, or the number is useless for spotting drift.
/// </remarks>
public sealed record MapResult(bool ShowChanged, int EpisodesAdded, int EpisodesUpdated)
{
    public static MapResult Unchanged { get; } = new(false, 0, 0);

    public bool AnyChange => ShowChanged || EpisodesAdded > 0 || EpisodesUpdated > 0;
}
