using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Shows.Application;
using Shows.Domain;
using Shows.Infrastructure.Configuration;
using Shows.Infrastructure.Persistence;
using Shows.Infrastructure.Tvmaze;

namespace Shows.Infrastructure.Jobs;

/// <summary>
/// The body shared by <c>EpisodeRefresh</c> (7.1) and <c>StatusRecheck</c> (7.2). Both are the same
/// three moves — <b>select → refresh source rows → map</b> (SPEC R11) — and differ only in which shows
/// they select and what they log. Neither contains mapping logic, and neither selects on
/// <c>user_show.category_id</c> (SPEC R2): the category is the user's choice, the status is the provider's.
/// </summary>
/// <remarks>
/// <para>
/// <b>Selection is on the enum, never a raw string</b> (R12), and always <c>deleted_at IS NULL</c> (R8) —
/// both enforced by the shared repository query, not re-implemented here. <c>EpisodeRefresh</c> selects
/// everything that is NOT Ended/Canceled (airing, unknown, and anything that failed to map) so an airing
/// show's catalog never silently rots; <c>StatusRecheck</c> selects exactly Ended/Canceled to catch a show
/// that resumed.
/// </para>
/// <para>
/// <b>items_changed is measured after the mapper</b>, not after the fetch (step 7.x): a run that fetched
/// every show but changed nothing reports 0, which is the only number that means anything for spotting drift.
/// </para>
/// <para>
/// <b>R14:</b> a transport failure (TVmaze unreachable) throws <see cref="TvmazeUnavailableException"/> and
/// is counted as a per-show failure, leaving that show untouched for the next run — it is never counted as
/// "no change" and never aborts the whole run. A genuine "TVmaze has no show N" (the mapper's own throw) is
/// likewise a per-show failure, not a crash.
/// </para>
/// <para>
/// <b>Lease-guarded</b> (ADR-028) with a heartbeat; a renewal that updates zero rows stops the run.
/// </para>
/// </remarks>
internal sealed class ShowRefreshJob(
    ShowsDbContext db,
    ITrackedShowRepository trackedShows,
    TvmazeShowMapper mapper,
    IWriterLease lease,
    IIdGenerator idGenerator,
    ShowsOptions options,
    ILogger<ShowRefreshJob> logger)
{
    // progress is optional and LAST so the existing console/test call RunAsync(mode, ct) still binds;
    // only the in-process Manage path passes it.
    public async Task<RefreshReport> RunAsync(
        RefreshMode mode, CancellationToken cancellationToken, IProgress<JobProgress>? progress = null)
    {
        var leaseName = mode == RefreshMode.EpisodeRefresh ? "episode_refresh" : "status_recheck";
        var jobName = mode == RefreshMode.EpisodeRefresh ? "EpisodeRefresh" : "StatusRecheck";

        await using var handle = await lease.TryAcquireAsync(leaseName, cancellationToken);
        if (handle is null)
        {
            logger.LogWarning("Another writer holds the lease; {Job} refused.", jobName);
            return new RefreshReport(jobName, Refused: true, 0, 0, 0);
        }

        var startedAt = DateTimeOffset.UtcNow;
        // job_run.id is client-minted (ValueGenerated.Never, ADR-026/027) — the SQLite provider would
        // otherwise treat the INTEGER PK as a store rowid and collide on a second run.
        var run = new JobRun { Id = idGenerator.NextId(), JobName = jobName, StartedAt = startedAt };
        db.JobRuns.Add(run);
        await db.SaveChangesAsync(cancellationToken);

        // The one shared selection (SPEC R8/R12). EpisodeRefresh excludes Ended/Canceled (refresh more is
        // correct); StatusRecheck selects exactly Ended/Canceled (catch a show that resumed).
        var targets = mode == RefreshMode.EpisodeRefresh
            ? await trackedShows.ListActiveExcludingStatusAsync(
                [ShowStatus.Ended, ShowStatus.Canceled], cancellationToken)
            : await trackedShows.ListActiveByStatusAsync(
                [ShowStatus.Ended, ShowStatus.Canceled], cancellationToken);

        var cap = options.Tvmaze.MaxRequestsPerRun;
        if (cap > 0 && targets.Count > cap)
        {
            logger.LogInformation(
                "{Job}: {Total} shows to refresh, capped at {Cap} this run.", jobName, targets.Count, cap);
            targets = targets.Take(cap).ToList();
        }

        var renewInterval = TimeSpan.FromSeconds(options.Sync.LeaseRenewIntervalSeconds);
        var lastRenew = DateTimeOffset.UtcNow;
        var changed = 0;
        var failed = 0;
        var index = 0;

        foreach (var show in targets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            index++;

            // Progress at Information so a run is legible in the log file: which show, and how far
            // through the batch. A refresh over the network is slow enough that silence looks like a hang
            // (the same reasoning as the Promoter's per-show line).
            logger.LogInformation(
                "{Job} {Index}/{Total}: tvmaze {TvmazeId} ({Status}).",
                jobName, index, targets.Count, show.TvmazeId, show.Status);

            // Surface the same per-show step to an in-process caller (the Manage page). Null when the
            // console drives it. Reported before the work so the bar advances as each show begins.
            progress?.Report(new JobProgress(jobName, index, targets.Count, $"tvmaze {show.TvmazeId}"));

            if (DateTimeOffset.UtcNow - lastRenew >= renewInterval)
            {
                if (!await handle.RenewAsync(cancellationToken))
                {
                    logger.LogError(
                        "Lost the {Lease} lease after {Changed} changes; stopping.", leaseName, changed);
                    break;
                }

                lastRenew = DateTimeOffset.UtcNow;
            }

            // Capture the mapped status before the map so StatusRecheck can report a resume (Ended → Running)
            // without reading the source table (source-table containment, step 8.1). The mapper updates this
            // same tracked entity in place (same scoped context), so show.Status after the map is the new value.
            var oldStatus = show.Status;

            try
            {
                var result = await mapper.MapShowAsync(show.TvmazeId, cancellationToken);

                // Persist this show's mapped rows before moving on: each show is its own unit of work, so a
                // later failure cannot roll back an earlier show's committed change.
                await db.SaveChangesAsync(cancellationToken);

                if (result.AnyChange)
                {
                    changed++;

                    if (mode == RefreshMode.StatusRecheck && show.Status != oldStatus)
                    {
                        // The interesting result of StatusRecheck: a show that resumed. Logged at Information
                        // with the show id and both statuses (the enum transition — the raw strings live in the
                        // source table, which this job must not read; the enum is the meaningful signal).
                        logger.LogInformation(
                            "StatusRecheck: show {TvmazeId} status {Old} -> {New}.",
                            show.TvmazeId, oldStatus, show.Status);
                    }
                }
            }
            catch (Exception ex) when (ex is TvmazeUnavailableException or InvalidOperationException)
            {
                // R14: unreachable, or TVmaze genuinely has no show N. Leave this show for the next run.
                // Discard any partial writes from this show so the next show starts from a clean context.
                db.ChangeTracker.Clear();
                failed++;
                logger.LogWarning(ex, "{Job}: show {TvmazeId} failed; leaving for the next run.", jobName, show.TvmazeId);
            }
        }

        run.FinishedAt = DateTimeOffset.UtcNow;
        run.Succeeded = true;
        run.ItemsTotal = targets.Count;
        run.ItemsChanged = changed;
        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "{Job} done: {Total} selected, {Changed} changed, {Failed} failed.", jobName, targets.Count, changed, failed);

        return new RefreshReport(jobName, Refused: false, targets.Count, changed, failed);
    }
}

/// <summary>Which of the two refresh jobs is running. The body is shared; only selection and logging differ.</summary>
internal enum RefreshMode
{
    EpisodeRefresh,
    StatusRecheck,
}

/// <summary>What one refresh run did. <see cref="Refused"/> means another writer held the lease.</summary>
internal sealed record RefreshReport(string JobName, bool Refused, int Total, int Changed, int Failed);
