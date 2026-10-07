namespace Shows.Domain;

/// <summary>
/// A show's airing state, as decided by the provider vocabulary. Internal enum, never a raw
/// provider string (SPEC R12) — the raw text lives only in <c>tvmaze_show.status_raw</c>.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Unknown"/> is the fail-closed landing spot for any string the mapping dictionary does
/// not recognise, and it must never satisfy the "no more episodes expected" test
/// (<see cref="ShowStatusRules"/>). That is the whole point of the enum: the original bug was a
/// predicate comparing against <c>"Running Series"</c>, a myshows string TVmaze never returns, which
/// matched nothing and would have auto-finished every airing show.
/// </para>
/// <para>
/// <c>0 = Unknown</c> is deliberate: <c>tracked_show.status_id</c> defaults to 0
/// (<c>SCHEMA.md</c> §2), so an unset column lands on the safe value rather than on a real status.
/// </para>
/// </remarks>
public enum ShowStatus
{
    Unknown = 0,
    Running = 1,
    Ended = 2,
    Canceled = 3,
    ToBeDetermined = 4,

    /// <summary>
    /// TVmaze's <c>"In Development"</c>: announced but not yet airing. Like
    /// <see cref="ToBeDetermined"/> it is excluded from
    /// <see cref="ShowStatusRules.NoMoreEpisodesExpected"/> — a show that has not begun airing may
    /// still air, so it must never auto-finish (SPEC R12).
    /// </summary>
    InDevelopment = 5,
}
