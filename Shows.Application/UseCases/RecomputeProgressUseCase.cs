using Shows.Domain;

namespace Shows.Application.UseCases;

/// <summary>
/// Recomputes the four cached columns on a show and applies SPEC R5's consequence.
/// </summary>
/// <remarks>
/// <para>
/// <b>The single recompute entry point</b> (<c>ARCHITECTURE.md</c> §5). Import, toggle, and all four
/// jobs go through it, because "two code paths computing progress differently is the most likely
/// source of subtle bugs in this project". The arithmetic itself is
/// <see cref="ShowProgress.Compute"/>, a pure Domain function; this use-case owns the part Domain
/// cannot: deciding whether to <em>act</em> on eligibility.
/// </para>
/// <para>
/// <b>SPEC R6 lives here.</b> "Manual category changes always win" needs <c>category_changed_at</c>
/// and the current category — user data the Domain function must not know about (SPEC P4). So: if the
/// user moved the show out of <c>Finished</c> more recently than the last watch-state change, the
/// recompute leaves the category alone until the next change. It does not fight them.
/// </para>
/// </remarks>
public sealed class RecomputeProgressUseCase(
    ITrackedShowRepository trackedShows,
    IEpisodeRepository episodes,
    IUserShowRepository userShows,
    IUnitOfWork unitOfWork)
{
    /// <summary>
    /// Recomputes one show. Returns the new progress, or null when the show no longer exists.
    /// </summary>
    public async Task<ShowProgress?> ExecuteAsync(
        long trackedShowId, CancellationToken cancellationToken)
    {
        var show = await trackedShows.FindByIdAsync(trackedShowId, cancellationToken)
            .ConfigureAwait(false);
        if (show is null)
        {
            return null;
        }

        var showEpisodes = await episodes.ListByShowIdAsync(trackedShowId, cancellationToken)
            .ConfigureAwait(false);
        var watchedIds = await episodes.ListWatchesByShowIdAsync(trackedShowId, cancellationToken)
            .ConfigureAwait(false);
        var watchedSet = watchedIds.Select(w => w.EpisodeId).ToHashSet();

        var facts = showEpisodes
            .Select(e => new EpisodeProgressFact(e.EpisodeType, watchedSet.Contains(e.Id)))
            .ToList();

        var progress = ShowProgress.Compute(show.Status, facts);

        show.WatchedRegular = progress.WatchedRegular;
        show.TotalRegular = progress.TotalRegular;
        show.HasUnwatchedSpecials = progress.HasUnwatchedSpecials;
        show.AutoFinishEligible = progress.AutoFinishEligible;

        if (progress.AutoFinishEligible && show.Category != ShowCategory.Finished)
        {
            // R5: move to Finished without prompting. R6: unless the user moved it out of Finished
            // after their most recent watch change, in which case their choice stands.
            var user = show.User;
            var lastWatchedAt = watchedIds.Count == 0
                ? default(DateTimeOffset?)
                : watchedIds.Max(w => w.WatchedAt);

            var userOverrode = user is not null
                && lastWatchedAt is not null
                && user.CategoryChangedAt > lastWatchedAt;

            if (!userOverrode)
            {
                await userShows
                    .ChangeCategoryAsync(
                        show.Id, ShowCategory.Finished, DateTimeOffset.UtcNow, cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return progress;
    }
}
