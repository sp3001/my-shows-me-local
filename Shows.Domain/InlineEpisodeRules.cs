namespace Shows.Domain;

/// <summary>
/// The single rule for whether an episode appears in the <b>inline</b> season list on a My Shows list
/// card (improvement 4). The list is "what is left to watch", so a row is listable only when it is a
/// regular, is not yet watched, and has already aired. The details page does not use this — it shows
/// every episode so the user can also un-watch (improvement 11).
/// </summary>
/// <remarks>
/// <para>
/// Owned by Domain so the predicate is testable and has exactly one home, the same way
/// <see cref="SeasonEpisodeOrdering"/> owns ordering. The UI supplies the episode, its watched flag, and
/// "today"; this decides membership. Keeping it here stops the filter from drifting between the eager
/// (expanded-season) and lazy (collapsed-season) load paths.
/// </para>
/// <para>
/// <b>Already-aired</b> means <see cref="Episode.AirDate"/> is known and not in the future relative to
/// <paramref name="today"/> (UTC). A null air date is excluded: an episode whose air date TVmaze never
/// supplied is not something the user can have watched yet, so it is not "left to watch" either — it
/// surfaces on the details page instead. <b>Insignificant specials</b> are excluded (SPEC R4 /
/// improvement 6 / ADR-034): they live only in the details page's dedicated section, never inline.
/// <b>Significant specials</b> are listable like regulars (ADR-034) — they count toward progress and
/// appear in the season grid, so they belong in "what is left to watch" too.
/// </para>
/// </remarks>
public static class InlineEpisodeRules
{
    /// <summary>
    /// True when <paramref name="episode"/> should appear in a card's inline season list: a regular or
    /// significant special, unwatched, and already aired on or before <paramref name="today"/>.
    /// </summary>
    public static bool IsListable(Episode episode, bool isWatched, DateOnly today)
    {
        ArgumentNullException.ThrowIfNull(episode);

        // Insignificant specials are never inline (R4 / improvement 6 / ADR-034) — they belong to the
        // details page section. Significant specials are listable like regulars.
        if (episode.EpisodeType == EpisodeType.InsignificantSpecial)
        {
            return false;
        }

        // The inline list is "what is left to watch" (improvement 4): a watched episode drops out.
        if (isWatched)
        {
            return false;
        }

        // Already-aired only: a known air date on or before today. A null air date is excluded.
        return episode.AirDate is { } airDate && airDate <= today;
    }
}
