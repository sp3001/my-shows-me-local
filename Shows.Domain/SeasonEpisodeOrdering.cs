namespace Shows.Domain;

/// <summary>
/// The ordering used by the show-details grid (improvement 11): newest season first, and within a
/// season the newest episode first. Owned by Domain so the comparators are testable and have exactly
/// one home — the UI supplies the rows, this decides their order.
/// </summary>
/// <remarks>
/// <para>
/// Seasons sort by <see cref="Season.SeasonNumber"/> descending. Episodes sort by
/// <see cref="Episode.AirDate"/> descending, then <see cref="Episode.EpisodeNumber"/> descending.
/// </para>
/// <para>
/// <b>Null handling (deliberate, improvement 11):</b> an episode with no air date sorts <em>last</em>
/// within its season (treated as oldest), and an episode with no episode number sorts last among
/// episodes sharing an air date. Nulls never throw — <c>EpisodeNumber</c> is null for IMDb legacy rows
/// and <c>AirDate</c> is null whenever TVmaze had none, so a comparator that assumed a value would
/// crash on real data.
/// </para>
/// </remarks>
public static class SeasonEpisodeOrdering
{
    /// <summary>Highest season number first.</summary>
    public static IComparer<Season> SeasonsDescending { get; } =
        Comparer<Season>.Create((a, b) => b.SeasonNumber.CompareTo(a.SeasonNumber));

    /// <summary>
    /// Newest air date first, then highest episode number. A null air date is treated as oldest
    /// (sorts last); a null episode number sorts last within an equal air date.
    /// </summary>
    public static IComparer<Episode> EpisodesDescending { get; } =
        Comparer<Episode>.Create(CompareEpisodes);

    private static int CompareEpisodes(Episode? a, Episode? b)
    {
        if (ReferenceEquals(a, b))
        {
            return 0;
        }
        if (a is null)
        {
            return 1; // a null row sorts last
        }
        if (b is null)
        {
            return -1;
        }

        // Air date descending; a null air date is "oldest" so it sorts after any dated episode.
        var byDate = CompareAirDatesDescending(a.AirDate, b.AirDate);
        if (byDate != 0)
        {
            return byDate;
        }

        // Episode number descending; a null number sorts last among equal air dates.
        return CompareNumbersDescending(a.EpisodeNumber, b.EpisodeNumber);
    }

    // Descending by date, nulls last: both null → equal; a null → a after b; b null → a before b.
    private static int CompareAirDatesDescending(DateOnly? a, DateOnly? b)
    {
        if (a is not { } av)
        {
            return b is null ? 0 : 1;
        }
        if (b is not { } bv)
        {
            return -1;
        }
        return bv.CompareTo(av);
    }

    // Descending by number, nulls last.
    private static int CompareNumbersDescending(int? a, int? b)
    {
        if (a is not { } av)
        {
            return b is null ? 0 : 1;
        }
        if (b is not { } bv)
        {
            return -1;
        }
        return bv.CompareTo(av);
    }
}
