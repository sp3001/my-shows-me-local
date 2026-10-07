using Shows.Domain;

namespace Shows.Application.UseCases;

/// <summary>
/// One upcoming episode in the upcoming-episodes view, paired with the show it belongs to. The upcoming
/// tab is grouped by <b>date</b> (not by show), so the read model is a flat episode list — the UI groups
/// it into day blocks. The show travels with each row so a row can name its show and link to its details.
/// </summary>
/// <param name="Show">The tracked show (title/poster come from its cached columns).</param>
/// <param name="Episode">The upcoming episode (unknown-or-future air date, regular or significant special).</param>
public sealed record UpcomingEpisode(TrackedShow Show, Episode Episode);

/// <summary>
/// The upcoming-episodes view (improvement 4, regrouped by date): every Started show's episodes whose air
/// date is unknown or in the future, returned as one flat list ordered soonest-first. A show with nothing
/// upcoming contributes no rows.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a flat list.</b> The tab used to group by show → season; the user asked to group by <b>date</b>
/// instead (this-week / other blocks, each a date header over its episode rows). Grouping into blocks and
/// days is presentation, so it lives in the UI; this read model just supplies the episodes with their
/// shows, already ordered so the UI's grouping is a stable single pass.
/// </para>
/// <para>
/// <b>Ordering.</b> Air date ascending (soonest first), with unknown air dates last (they have no date to
/// place); ties break by show title, then season, then episode number. The clock is a parameter, so the
/// filter is pure and testable.
/// </para>
/// <para>
/// <b>Batched like the Started read.</b> One set-based query for all Started shows' upcoming episodes, not
/// one per show. Seasons are not needed here — the flat rows carry their own season number.
/// </para>
/// </remarks>
public sealed class GetUpcomingEpisodesUseCase(
    ITrackedShowRepository trackedShows,
    IEpisodeRepository episodes)
{
    /// <summary>
    /// Loads the upcoming episodes for the Started category. <paramref name="today"/> is a parameter (not
    /// read from the clock here) so the filter is pure and testable; the caller passes the UTC date.
    /// </summary>
    public async Task<IReadOnlyList<UpcomingEpisode>> ExecuteAsync(
        DateOnly today, CancellationToken cancellationToken)
    {
        var startedShows = await trackedShows
            .ListActiveByCategoryAsync(ShowCategory.Started, cancellationToken).ConfigureAwait(false);
        if (startedShows.Count == 0)
        {
            return [];
        }

        var showIds = startedShows.Select(s => s.Id).ToList();

        // The upcoming episodes (unknown-or-future, regulars + significant specials) across all Started
        // shows. Pair each with its show, then order soonest-first with unknown dates last.
        var showById = startedShows.ToDictionary(s => s.Id);

        var upcoming = await episodes
            .ListUpcomingByShowIdsAsync(showIds, today, cancellationToken).ConfigureAwait(false);

        var result = new List<UpcomingEpisode>(upcoming.Count);
        foreach (var episode in upcoming)
        {
            if (showById.TryGetValue(episode.ShowId, out var show))
            {
                result.Add(new UpcomingEpisode(show, episode));
            }
        }

        result.Sort(CompareByDateThenShow);
        return result;
    }

    // Soonest air date first; an unknown air date sorts last (it has no date to place). Ties break by show
    // title, then season number, then episode number (a null number sorts last within a season).
    private static int CompareByDateThenShow(UpcomingEpisode a, UpcomingEpisode b)
    {
        var byDate = CompareDateAscending(a.Episode.AirDate, b.Episode.AirDate);
        if (byDate != 0)
        {
            return byDate;
        }

        var byShow = string.Compare(a.Show.Title, b.Show.Title, StringComparison.OrdinalIgnoreCase);
        if (byShow != 0)
        {
            return byShow;
        }

        var bySeason = a.Episode.SeasonNumber.CompareTo(b.Episode.SeasonNumber);
        if (bySeason != 0)
        {
            return bySeason;
        }

        return CompareNumberAscending(a.Episode.EpisodeNumber, b.Episode.EpisodeNumber);
    }

    // Ascending by date, unknown last: both null → equal; a null → a after b; b null → a before b.
    private static int CompareDateAscending(DateOnly? a, DateOnly? b)
    {
        if (a is not { } av)
        {
            return b is null ? 0 : 1;
        }
        if (b is not { } bv)
        {
            return -1;
        }
        return av.CompareTo(bv);
    }

    // Ascending by number, null last.
    private static int CompareNumberAscending(int? a, int? b)
    {
        if (a is not { } av)
        {
            return b is null ? 0 : 1;
        }
        if (b is not { } bv)
        {
            return -1;
        }
        return av.CompareTo(bv);
    }
}
