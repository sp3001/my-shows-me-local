namespace Shows.Domain;

/// <summary>
/// One watched episode, with the moment it was watched.
/// </summary>
/// <remarks>
/// <para>
/// <b>Absence of a row means unwatched.</b> Rows rather than a boolean column, to keep history
/// available for future statistics. This is the most important table in the database: with
/// <see cref="UserShow"/> and <see cref="UiSeasonState"/> it is the only data that cannot be rebuilt
/// from the source tables (SPEC P4).
/// </para>
/// <para>
/// Untracking a show does not touch these rows (ADR-024). Deleting a row here is what "unwatched an
/// episode" means, and it is the one user action that is a real delete — because the absence is the
/// meaning.
/// </para>
/// </remarks>
public sealed class EpisodeWatch
{
    public long Id { get; set; }

    /// <summary>Denormalised so per-show progress is a single-table count.</summary>
    public long ShowId { get; set; }

    /// <summary>Unique: an episode is watched at most once.</summary>
    public long EpisodeId { get; set; }

    public DateTimeOffset WatchedAt { get; set; }
}
