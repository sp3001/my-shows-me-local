namespace Shows.Domain;

/// <summary>
/// The minimum an episode must report to participate in progress math: what kind of episode it is,
/// and whether the user has watched it.
/// </summary>
/// <param name="Type">
/// <see cref="EpisodeType.Regular"/> and <see cref="EpisodeType.Special"/> (a <em>significant</em>
/// special) both count toward progress; <see cref="EpisodeType.InsignificantSpecial"/> and
/// <see cref="EpisodeType.Unknown"/> do not (SPEC R4 / ADR-034).
/// </param>
/// <param name="IsWatched">Whether an <c>episode_watch</c> row exists. Absence of a row means
/// unwatched (<c>SCHEMA.md</c> §2 <c>episode_watch</c>).</param>
public readonly record struct EpisodeProgressFact(EpisodeType Type, bool IsWatched);

/// <summary>
/// The four cached columns on <c>tracked_show</c> (<c>SCHEMA.md</c> §2), computed as a pure function
/// of a show's status and its episodes. This is SPEC R5 and R7, and it is the single definition of
/// progress in the solution: <c>ARCHITECTURE.md</c> §5 names "two code paths computing progress
/// differently" as the most likely source of subtle bugs here, so every mutation path — import,
/// toggle, and all four jobs — funnels through this method.
/// </summary>
/// <param name="WatchedRegular">Cached as <c>watched_regular</c>. Counts regulars and significant
/// specials; excludes insignificant specials and Unknown (SPEC R4 / ADR-034).</param>
/// <param name="TotalRegular">Cached as <c>total_regular</c>. Same set as <see cref="WatchedRegular"/>.</param>
/// <param name="HasUnwatchedSpecials">Cached as <c>has_unwatched_specials</c>. Set by unwatched
/// <em>insignificant</em> specials only (ADR-034). Surfaces as a note on the show card; a filter for
/// it is deferred past v1.</param>
/// <param name="AutoFinishEligible">Cached as <c>auto_finish_eligible</c> (SPEC R5).</param>
public sealed record ShowProgress(
    int WatchedRegular,
    int TotalRegular,
    bool HasUnwatchedSpecials,
    bool AutoFinishEligible)
{
    /// <summary>
    /// Computes the cached columns.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>R5</b>: eligible when <c>total_regular &gt; 0</c>, every regular episode is watched, and
    /// the status means no more episodes are expected — see <see cref="ShowStatusRules"/>. A show
    /// whose status is <see cref="ShowStatus.Running"/> is never promoted even when fully watched,
    /// because more episodes are expected.
    /// </para>
    /// <para>
    /// <b>R7</b>: a show with no regular episodes is never eligible, and <see cref="ProgressFraction"/>
    /// reports <see langword="null"/> rather than dividing by zero. At least one such show exists in
    /// the real data (<c>totalEpisodes: 0</c>, <c>watchedEpisodes: 0</c> — <c>DATA_ANALYSIS.md</c>).
    /// </para>
    /// <para>
    /// <b>R6 is not handled here, deliberately.</b> "Manual category changes always win" is a
    /// question about whether to <em>act</em> on eligibility, which needs <c>category_changed_at</c>
    /// and the current category — user data this function must not know about (SPEC P4). It belongs
    /// to <c>RecomputeProgressUseCase</c>, which is also the only caller allowed to move a show
    /// between categories.
    /// </para>
    /// </remarks>
    public static ShowProgress Compute(ShowStatus status, IEnumerable<EpisodeProgressFact> episodes)
    {
        ArgumentNullException.ThrowIfNull(episodes);

        var watchedRegular = 0;
        var totalRegular = 0;
        var hasUnwatchedSpecials = false;

        foreach (var episode in episodes)
        {
            // Regulars and significant specials both count (SPEC R4 / ADR-034): a significant special
            // is a real episode the user is expected to watch, so it participates in the totals and in
            // the auto-finish test exactly like a regular.
            if (episode.Type is EpisodeType.Regular or EpisodeType.Special)
            {
                totalRegular++;
                if (episode.IsWatched)
                {
                    watchedRegular++;
                }
            }
            // Only insignificant specials drive the "unwatched specials" note (ADR-034). Unknown is
            // excluded on purpose: an unrecognised provider type is not counted as regular, so a
            // vocabulary change cannot silently inflate or deflate progress.
            else if (episode.Type == EpisodeType.InsignificantSpecial && !episode.IsWatched)
            {
                hasUnwatchedSpecials = true;
            }
        }

        var autoFinishEligible =
            totalRegular > 0
            && watchedRegular == totalRegular
            && ShowStatusRules.NoMoreEpisodesExpected(status);

        return new ShowProgress(watchedRegular, totalRegular, hasUnwatchedSpecials, autoFinishEligible);
    }

    /// <summary>
    /// <c>watched_regular / total_regular</c>, or <see langword="null"/> when there are no regular
    /// episodes (SPEC R7). A null is the correct answer for a degenerate show and renders as "no
    /// episodes yet"; zero would claim the user has watched nothing of a show that has nothing.
    /// </summary>
    public double? ProgressFraction => TotalRegular > 0 ? (double)WatchedRegular / TotalRegular : null;
}
