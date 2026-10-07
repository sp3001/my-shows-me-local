using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Shows.Application;
using Shows.Infrastructure.Configuration;
using Shows.Infrastructure.Persistence;
using Shows.Infrastructure.Tvmaze;

namespace Shows.Infrastructure.Jobs;

/// <summary>
/// The <c>RefreshUpdated</c> job body: ask TVmaze <b>once</b> which of its shows changed
/// (<c>/updates/shows</c> → <c>{ tvmazeId: updated }</c>), then re-fetch and re-map only the actively
/// tracked shows whose provider timestamp is newer than what we last stored. It is the cheap alternative
/// to <c>EpisodeRefresh</c>'s "refresh everything airing" sweep — one bulk call plus a fetch per genuinely
/// changed show, instead of a fetch per tracked show.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why one bulk call.</b> <c>/updates/shows</c> returns the update timestamp for every show on the site,
/// so the response is large; it is fetched once and cached (<see cref="HttpCacheStore"/>), never per show.
/// The comparison against our stored <c>provider_updated</c> decides who to refresh. The stored timestamp is
/// read through the mapper (<see cref="TvmazeShowMapper.GetProviderUpdatedAsync"/>) — source-table
/// containment (SPEC R11, step 8.1) forbids this job reaching into <c>tvmaze_show</c> directly.
/// </para>
/// <para>
/// <b>Selection is still <c>deleted_at IS NULL</c></b> (R8): a soft-deleted show is never refreshed, so the
/// bulk map is only ever consulted for shows the user actually tracks. A show absent from the update map, or
/// whose map timestamp is not newer than ours, is skipped — no signal means no refresh.
/// </para>
/// <para>
/// <b>Idempotence:</b> a refresh writes the new payload through the mapper, which stamps the source row's
/// <c>provider_updated</c> to the value we just acted on. A second run sees <c>map[id] == stored</c> and
/// skips it, so <c>items_changed</c> reports 0 on a re-run — the same honesty the other refresh jobs keep.
/// </para>
/// <para>
/// <b>R14:</b> the bulk map fetch is the one whole-run network op — if TVmaze is unreachable the job cannot
/// know what changed, so it aborts without touching anything (<see cref="RefreshUpdatedReport.ProviderUnavailable"/>).
/// A per-show re-fetch failure (the mapper's own fetch) is a per-show failure, leaving that show for the next
/// run, never a crash and never counted as "no change".
/// </para>
/// <para>
/// <b>Lease-guarded</b> (ADR-028) with a heartbeat; a renewal that updates zero rows stops the run.
/// </para>
/// </remarks>
internal sealed class RefreshUpdatedShowJob(
    ShowsDbContext db,
    ITrackedShowRepository trackedShows,
    ICatalogProvider provider,
    TvmazeShowMapper mapper,
    IWriterLease lease,
    IIdGenerator idGenerator,
    ShowsOptions options,
    ILogger<RefreshUpdatedShowJob> logger)
{
    // progress is optional and LAST so the console/test call RunAsync(ct) still binds; only the
    // in-process Manage path passes it.
    public async Task<RefreshUpdatedReport> RunAsync(
        CancellationToken cancellationToken, IProgress<JobProgress>? progress = null)
    {
        const string jobName = "RefreshUpdated";

        await using var handle = await lease.TryAcquireAsync("refresh_updated", cancellationToken);
        if (handle is null)
        {
            logger.LogWarning("Another writer holds the lease; {Job} refused.", jobName);
            return new RefreshUpdatedReport(Refused: true, ProviderUnavailable: false, 0, 0, 0);
        }

        var startedAt = DateTimeOffset.UtcNow;
        // job_run.id is client-minted (ValueGenerated.Never, ADR-026/027).
        var run = new JobRun { Id = idGenerator.NextId(), JobName = jobName, StartedAt = startedAt };
        db.JobRuns.Add(run);
        await db.SaveChangesAsync(cancellationToken);

        // The one bulk fetch. R14: unreachable → we cannot know what changed, so abort without touching
        // anything rather than reporting a clean run that refreshed nothing.
        IReadOnlyDictionary<int, long> updates;
        try
        {
            updates = await provider.GetShowUpdateTimestampsAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw; // the caller cancelled; not a provider failure
        }
        catch (TvmazeUnavailableException ex)
        {
            logger.LogError(ex, "{Job}: TVmaze updates endpoint unreachable; aborting without changes.", jobName);
            run.FinishedAt = DateTimeOffset.UtcNow;
            run.Succeeded = false;
            await db.SaveChangesAsync(cancellationToken);
            return new RefreshUpdatedReport(Refused: false, ProviderUnavailable: true, 0, 0, 0);
        }

        var targets = await trackedShows.ListActiveAsync(cancellationToken);

        var renewInterval = TimeSpan.FromSeconds(options.Sync.LeaseRenewIntervalSeconds);
        var lastRenew = DateTimeOffset.UtcNow;
        var cap = options.Tvmaze.MaxRequestsPerRun;
        var changed = 0;
        var failed = 0;
        var refreshed = 0;
        var index = 0;

        foreach (var show in targets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            index++;

            // Read the stored timestamp through the mapper (never the source table directly). A show whose
            // provider timestamp is not strictly newer than ours — or absent from the map — needs no work.
            var stored = await mapper.GetProviderUpdatedAsync(show.TvmazeId, cancellationToken);
            if (!updates.TryGetValue(show.TvmazeId, out var latest) || (stored.HasValue && latest <= stored.Value))
            {
                continue;
            }

            // The cap bounds the number of per-show re-fetches (the bulk map is a single request).
            if (cap > 0 && refreshed >= cap)
            {
                logger.LogInformation(
                    "{Job}: reached the {Cap}-show cap this run; the rest wait for the next run.", jobName, cap);
                break;
            }

            refreshed++;

            logger.LogInformation(
                "{Job} {Index}/{Total}: tvmaze {TvmazeId} updated {Stored} -> {Latest}.",
                jobName, index, targets.Count, show.TvmazeId, stored?.ToString() ?? "none", latest);

            progress?.Report(new JobProgress(jobName, index, targets.Count, $"tvmaze {show.TvmazeId}"));

            if (DateTimeOffset.UtcNow - lastRenew >= renewInterval)
            {
                if (!await handle.RenewAsync(cancellationToken))
                {
                    logger.LogError("Lost the refresh_updated lease after {Changed} changes; stopping.", changed);
                    break;
                }

                lastRenew = DateTimeOffset.UtcNow;
            }

            try
            {
                // MapShowAsync(int) re-fetches the show + episodes and writes the source rows (stamping the
                // new provider_updated) and the mapped rows. Each show is its own unit of work.
                var result = await mapper.MapShowAsync(show.TvmazeId, cancellationToken);
                await db.SaveChangesAsync(cancellationToken);

                if (result.AnyChange)
                {
                    changed++;
                }
            }
            catch (Exception ex) when (ex is TvmazeUnavailableException or InvalidOperationException)
            {
                // R14: unreachable, or TVmaze genuinely has no show N. Leave this show for the next run;
                // discard any partial writes so the next show starts from a clean context.
                db.ChangeTracker.Clear();
                failed++;
                logger.LogWarning(ex, "{Job}: show {TvmazeId} failed; leaving for the next run.", jobName, show.TvmazeId);
            }
        }

        run.FinishedAt = DateTimeOffset.UtcNow;
        run.Succeeded = true;
        run.ItemsTotal = refreshed;
        run.ItemsChanged = changed;
        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "{Job} done: {Tracked} tracked, {Refreshed} needed refresh, {Changed} changed, {Failed} failed.",
            jobName, targets.Count, refreshed, changed, failed);

        return new RefreshUpdatedReport(Refused: false, ProviderUnavailable: false, refreshed, changed, failed);
    }
}

/// <summary>
/// What one <c>RefreshUpdated</c> run did. <see cref="Refused"/> means another writer held the lease;
/// <see cref="ProviderUnavailable"/> means TVmaze could not be reached for the bulk map, so nothing was
/// refreshed (R14) — distinct from a clean run that found no changes.
/// </summary>
internal sealed record RefreshUpdatedReport(
    bool Refused, bool ProviderUnavailable, int Total, int Changed, int Failed);
