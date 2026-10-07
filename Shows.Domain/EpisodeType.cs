namespace Shows.Domain;

/// <summary>
/// Whether an episode counts toward progress. Internal enum, never a raw provider string
/// (SPEC R12) — the raw text lives only in <c>tvmaze_episode.type_raw</c>.
/// </summary>
/// <remarks>
/// <para>
/// TVmaze's <c>type</c> distinguishes regular from special independently of season number, which is
/// strictly better information than <c>season_number == 0</c> because some shows number specials
/// in-season (SPEC R4). <c>season_number == 0</c> is only the fallback where IMDb data is the sole
/// source.
/// </para>
/// <para>
/// TVmaze splits specials into two kinds and the distinction is load-bearing for progress
/// (SPEC R4 / ADR-034): <c>significant_special</c> is a real episode the user is expected to watch
/// (<see cref="Special"/> — counts toward progress and auto-finish, shown in the season grid with a
/// marker), while <c>insignificant_special</c> is filler (<see cref="InsignificantSpecial"/> —
/// excluded from every progress figure, shown only in the details page's separate Specials section).
/// </para>
/// <para>
/// <see cref="Unknown"/> is fail-closed and is treated as <em>not regular</em>, so an unrecognised
/// type is excluded from <c>watched_regular</c> / <c>total_regular</c> rather than silently counted.
/// It is also rendered with a distinct marker so a vocabulary gap is visible in the UI rather than
/// masquerading as a regular.
/// </para>
/// </remarks>
public enum EpisodeType
{
    /// <summary>Unrecognised provider type. Fail-closed: excluded from progress, shown with a marker.</summary>
    Unknown = 0,

    /// <summary>A normal episode. Counts toward progress and auto-finish.</summary>
    Regular = 1,

    /// <summary>
    /// A <em>significant</em> special (TVmaze <c>significant_special</c>). Behaves like a regular for
    /// progress and auto-finish (SPEC R4 / ADR-034) but is marked in the UI.
    /// </summary>
    Special = 2,

    /// <summary>
    /// An <em>insignificant</em> special (TVmaze <c>insignificant_special</c>). Excluded from every
    /// progress figure and from auto-finish; shown only in the details page's Specials section.
    /// </summary>
    InsignificantSpecial = 3,
}
