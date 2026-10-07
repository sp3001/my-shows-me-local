using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Shows.Application;
using Shows.Infrastructure.Configuration;
using Shows.Infrastructure.Persistence;

namespace Shows.Infrastructure.Jobs;

/// <summary>
/// The <c>Enrichment --akas</c> job body (step 7.5): for each actively-tracked show, fetch its
/// <c>/shows/{id}/akas</c>, write the source rows through the mapper, and map the target-country aka into
/// <c>tracked_show.title_localized</c>. Decoupled from the IMDb jobs and from open question 1 (that
/// question is episode-set union vs flag, unrelated to titles).
/// </summary>
/// <remarks>
/// <para>
/// Selection is the shared repository query (<c>deleted_at IS NULL</c>, R8) — a soft-deleted show is never
/// fetched. Lease-guarded (ADR-028) with a heartbeat; a renewal that updates zero rows stops the run.
/// </para>
/// <para>
/// <b>R14:</b> a transport failure (TVmaze unreachable) throws <see cref="Tvmaze.TvmazeUnavailableException"/>
/// and is a per-show failure — the show is left for the next run, never counted as "no change", never a
/// crash. A genuine empty aka list is a real "TVmaze has no akas" and is not a failure.
/// </para>
/// <para>
/// <b>items_changed</b> is the mapper's mapped-title change, not source-row churn: a run that re-fetched
/// every show but changed no localized title reports 0 (idempotence).
/// </para>
/// </remarks>
internal sealed class AkasJob(
    ShowsDbContext db,
    ITrackedShowRepository trackedShows,
    ICatalogProvider provider,
    TvmazeShowMapper mapper,
    IWriterLease lease,
    IIdGenerator idGenerator,
    ShowsOptions options,
    ILogger<AkasJob> logger)
{
    // progress is optional and LAST so the existing console/test call RunAsync(ct) still binds; only the
    // in-process Manage path passes it.
    public async Task<AkasReport> RunAsync(CancellationToken cancellationToken, IProgress<JobProgress>? progress = null)
    {
        await using var handle = await lease.TryAcquireAsync("enrichment_akas", cancellationToken);
        if (handle is null)
        {
            logger.LogWarning("Another writer holds the lease; Enrichment --akas refused.");
            return new AkasReport(Refused: true, 0, 0, 0);
        }

        var startedAt = DateTimeOffset.UtcNow;
        var run = new JobRun { Id = idGenerator.NextId(), JobName = "Enrichment.Akas", StartedAt = startedAt };
        db.JobRuns.Add(run);
        await db.SaveChangesAsync(cancellationToken);

        var targets = await trackedShows.ListActiveAsync(cancellationToken);

        var cap = options.Tvmaze.MaxRequestsPerRun;
        if (cap > 0 && targets.Count > cap)
        {
            logger.LogInformation("Enrichment --akas: {Total} shows, capped at {Cap} this run.", targets.Count, cap);
            targets = targets.Take(cap).ToList();
        }

        var targetCountry = options.Localization.TargetCountryCode;
        var renewInterval = TimeSpan.FromSeconds(options.Sync.LeaseRenewIntervalSeconds);
        var lastRenew = DateTimeOffset.UtcNow;
        var changed = 0;
        var failed = 0;
        var index = 0;

        foreach (var show in targets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            index++;

            // Progress at Information so a run is legible in the log file (see the Promoter's per-show line).
            logger.LogInformation(
                "EnrichmentAkas {Index}/{Total}: tvmaze {TvmazeId}.",
                index, targets.Count, show.TvmazeId);

            // Surface the same step to an in-process caller (the Manage page); null from the console.
            progress?.Report(new JobProgress("Enrichment --akas", index, targets.Count, $"tvmaze {show.TvmazeId}"));

            if (DateTimeOffset.UtcNow - lastRenew >= renewInterval)
            {
                if (!await handle.RenewAsync(cancellationToken))
                {
                    logger.LogError("Lost the enrichment_akas lease after {Changed} changes; stopping.", changed);
                    break;
                }

                lastRenew = DateTimeOffset.UtcNow;
            }

            try
            {
                var akas = await provider.GetAkasAsync(show.TvmazeId, cancellationToken);

                // The mapper writes the source rows and maps the pick; it does not SaveChanges here, so the
                // job owns the per-show commit (each show is its own unit of work).
                var result = await mapper.MapAkasAsync(
                    show.TvmazeId, akas, targetCountry, cancellationToken);
                await db.SaveChangesAsync(cancellationToken);

                if (result.Changed)
                {
                    changed++;
                }
            }
            catch (Exception ex) when (ex is Tvmaze.TvmazeUnavailableException)
            {
                // R14: unreachable. Leave this show for the next run; discard any partial writes.
                db.ChangeTracker.Clear();
                failed++;
                logger.LogWarning(ex, "Enrichment --akas: show {TvmazeId} failed; leaving for the next run.", show.TvmazeId);
            }
        }

        run.FinishedAt = DateTimeOffset.UtcNow;
        run.Succeeded = true;
        run.ItemsTotal = targets.Count;
        run.ItemsChanged = changed;
        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Enrichment --akas done: {Total} selected, {Changed} titles set, {Failed} failed.",
            targets.Count, changed, failed);

        return new AkasReport(Refused: false, targets.Count, changed, failed);
    }
}

/// <summary>What one <c>--akas</c> run did. <see cref="Refused"/> means another writer held the lease.</summary>
internal sealed record AkasReport(bool Refused, int Total, int Changed, int Failed);
