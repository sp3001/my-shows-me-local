using Shows.Domain;

namespace Shows.Application.UseCases;

/// <summary>
/// One show row in the Updates feed. The three show-shaped sections differ only in <em>which</em> date they
/// sort and display, so they share this row: the UI reads <see cref="At"/> for the date column and
/// <see cref="UpdatesFeedRow.Show"/>'s <c>Status</c> when a section also shows the current status.
/// </summary>
/// <param name="Show">The tracked show (title/poster come from its cached columns).</param>
/// <param name="At">The event date this section is ordered and labelled by.</param>
public sealed record UpdatesFeedRow(TrackedShow Show, DateTimeOffset At);

/// <summary>
/// One recently-watched episode: the show, the episode, and the moment it was marked.
/// </summary>
public sealed record RecentlyWatchedRow(TrackedShow Show, Episode Episode, DateTimeOffset WatchedAt);

/// <summary>
/// The Updates tab's four sections. Each is a separate predicate over a different column, so the use case
/// supplies four ordered lists rather than one flat one — unlike the Upcoming tab, where the grouping is
/// purely presentational and the UI does it.
/// </summary>
/// <param name="Ended">Shows that became terminal (Ended/Canceled) inside the status window, newest first.</param>
/// <param name="Renewed">Shows that moved out of a terminal status inside the status window, newest first.</param>
/// <param name="Updated">Shows TVmaze itself changed inside the provider window, newest first.</param>
/// <param name="RecentlyWatched">Episodes marked watched inside the provider window, newest first.</param>
public sealed record UpdatesFeed(
    IReadOnlyList<UpdatesFeedRow> Ended,
    IReadOnlyList<UpdatesFeedRow> Renewed,
    IReadOnlyList<UpdatesFeedRow> Updated,
    IReadOnlyList<RecentlyWatchedRow> RecentlyWatched);

/// <summary>
/// The Updates tab: what changed recently, from four angles — shows that ended, shows that came back,
/// shows TVmaze edited, and episodes the user watched.
/// </summary>
/// <remarks>
/// <para>
/// <b>Sections 1–3 read the active aggregate and filter in memory.</b> <c>ListActiveAsync</c> already
/// applies <c>deleted_at IS NULL</c> in the one place it is allowed (ADR-024), and the active set is a few
/// hundred rows, so three windowed LINQ <c>WHERE</c>s on the repository would be three more places for
/// that predicate to be forgotten — for no measurable gain. It also sidesteps the standing EF-Core-on-
/// SQLite constraint that a <c>DateTimeOffset</c> comparison cannot be translated at all.
/// </para>
/// <para>
/// <b>Section 4 is the exception</b> and goes to SQL, because <c>episode_watch</c> is the one table here
/// with five figures of rows and it is not show-scoped by the request. Its cutoff is a raw-SQL string
/// comparison (see <see cref="IEpisodeRepository.ListWatchesSinceAsync"/>), and the rows it returns are
/// re-resolved through an active-only show lookup so an untracked show's marks drop out rather than
/// leaking a show the user removed (ADR-024).
/// </para>
/// <para>
/// <b>Why the two windows differ.</b> Status news is rare and worth a longer look-back; provider edits and
/// watch marks are frequent, so 30 days already fills the section. The windows are constants rather than
/// configuration — they shape one personal view, and a knob nobody would tune is just another thing to
/// document. The clock is a parameter so the whole feed is pure and testable.
/// </para>
/// </remarks>
public sealed class GetUpdatesFeedUseCase(
    ITrackedShowRepository trackedShows,
    IEpisodeRepository episodes)
{
    /// <summary>How far back the Ended and Renewed sections look.</summary>
    public const int StatusWindowDays = 60;

    /// <summary>How far back the Updated and Recently-watched sections look.</summary>
    public const int RecentWindowDays = 30;

    public async Task<UpdatesFeed> ExecuteAsync(
        DateTimeOffset now, CancellationToken cancellationToken)
    {
        var statusCutoff = now.AddDays(-StatusWindowDays);
        var recentCutoff = now.AddDays(-RecentWindowDays);

        var shows = await trackedShows.ListActiveAsync(cancellationToken).ConfigureAwait(false);

        // Ended: terminal now, and became terminal inside the window. The terminal test is
        // ShowStatusRules.NoMoreEpisodesExpected — the one R12 definition — so the predicate and the
        // mapper's rule for stamping the date cannot drift apart.
        var ended = shows
            .Where(s => ShowStatusRules.NoMoreEpisodesExpected(s.Status)
                        && s.StatusChangedAt is { } endedAt && endedAt >= statusCutoff)
            .Select(s => new UpdatesFeedRow(s, s.StatusChangedAt!.Value))
            .ToList();
        ended.Sort(CompareByDateThenTitle);

        // Renewed: can still air now, and left a terminal status inside the window. The status test is the
        // same rule negated, which is what keeps a show that re-ended out of this section — its
        // StatusRenewedAt still says "renewed in November" but it has since ended again, and listing it as
        // renewed would be wrong.
        var renewed = shows
            .Where(s => !ShowStatusRules.NoMoreEpisodesExpected(s.Status)
                        && s.StatusRenewedAt is { } renewedAt && renewedAt >= statusCutoff)
            .Select(s => new UpdatesFeedRow(s, s.StatusRenewedAt!.Value))
            .ToList();
        renewed.Sort(CompareByDateThenTitle);

        // Updated: TVmaze's own 'updated' instant, copied onto the mapped row (ADR-038). Not
        // tracked_show.mapped_at, which also moves on our own recomputes and would report churn the
        // provider never caused.
        var updated = shows
            .Where(s => s.TvmazeUpdatedAt is { } updatedAt && updatedAt >= recentCutoff)
            .Select(s => new UpdatesFeedRow(s, s.TvmazeUpdatedAt!.Value))
            .ToList();
        updated.Sort(CompareByDateThenTitle);

        var watched = await LoadRecentlyWatchedAsync(recentCutoff, cancellationToken)
            .ConfigureAwait(false);

        return new UpdatesFeed(ended, renewed, updated, watched);
    }

    // Watch rows in the window, then their episodes, then their shows — three bounded reads. The show
    // lookup is active-only, which is what drops an untracked show's marks: episode_watch survives
    // untracking by design (ADR-024), so the rows exist whether or not the user still tracks the show.
    private async Task<IReadOnlyList<RecentlyWatchedRow>> LoadRecentlyWatchedAsync(
        DateTimeOffset since, CancellationToken cancellationToken)
    {
        var watches = await episodes.ListWatchesSinceAsync(since, cancellationToken)
            .ConfigureAwait(false);
        if (watches.Count == 0)
        {
            return [];
        }

        var episodeIds = watches.Select(w => w.EpisodeId).ToList();
        var episodesById = (await episodes.ListByIdsAsync(episodeIds, cancellationToken)
            .ConfigureAwait(false)).ToDictionary(e => e.Id);

        var showIds = watches.Select(w => w.ShowId).Distinct().ToList();
        var showsById = (await trackedShows.ListActiveByIdsAsync(showIds, cancellationToken)
            .ConfigureAwait(false)).ToDictionary(s => s.Id);

        // Built in the order the watch rows arrive (newest first), so no re-sort is needed and an episode
        // whose row vanished, or a show the user untracked, is skipped rather than guessed at.
        var rows = new List<RecentlyWatchedRow>(watches.Count);
        foreach (var watch in watches)
        {
            if (episodesById.TryGetValue(watch.EpisodeId, out var episode)
                && showsById.TryGetValue(watch.ShowId, out var show))
            {
                rows.Add(new RecentlyWatchedRow(show, episode, watch.WatchedAt));
            }
        }

        return rows;
    }

    // Newest event first; ties break by title so the section order is stable across reads.
    private static int CompareByDateThenTitle(UpdatesFeedRow a, UpdatesFeedRow b)
    {
        var byDate = b.At.CompareTo(a.At);
        return byDate != 0
            ? byDate
            : string.Compare(a.Show.Title, b.Show.Title, StringComparison.OrdinalIgnoreCase);
    }
}
