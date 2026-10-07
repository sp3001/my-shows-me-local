using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Shows.Application;
using Shows.Infrastructure.Configuration;
using Shows.Infrastructure.Persistence;

namespace Shows.Infrastructure.Jobs;

/// <summary>
/// The <c>Enrichment --imdb</c> job body (step 7.3, ADR-031): for each actively-tracked show that carries
/// an <c>imdb_id</c>, fill episode gaps from the IMDb source tables (TVmaze authoritative, IMDb completes)
/// and record every disagreement in <c>episode_mismatch</c> for the details page. It never unions an
/// IMDb-only episode into <c>episode</c> and never overwrites a TVmaze value.
/// </summary>
/// <remarks>
/// <para>
/// Selection is the shared repository query (<c>deleted_at IS NULL</c>, R8) filtered to shows with an
/// <c>imdb_id</c> — a soft-deleted show is never enriched, and a show without an IMDb id has nothing to
/// reconcile. Lease-guarded (ADR-028) as <c>enrichment_imdb</c> with a heartbeat; a renewal that updates
/// zero rows stops the run.
/// </para>
/// <para>
/// <b>No transport here:</b> the IMDb data is already on disk in <c>imdb_*</c> (loaded by 7.4), so there is
/// no R14 fetch-failure path — a per-show failure would be a data error, not a provider outage. Each show
/// is its own unit of work (commit per show), so one bad show cannot poison the run.
/// </para>
/// <para>
/// <b>items_changed</b> is the mapper's episode field-fill signal, not mismatch-row churn: a run that
/// recomputed every show's mismatches but filled no new gap reports 0 (idempotence).
/// </para>
/// </remarks>
internal sealed class ImdbEnrichmentJob(
    ShowsDbContext db,
    ITrackedShowRepository trackedShows,
    TvmazeShowMapper mapper,
    IWriterLease lease,
    IIdGenerator idGenerator,
    ShowsOptions options,
    ILogger<ImdbEnrichmentJob> logger)
{
    // progress is optional and LAST so the existing console/test call RunAsync(ct) still binds; only the
    // in-process Manage path passes it.
    public async Task<ImdbEnrichmentReport> RunAsync(CancellationToken cancellationToken, IProgress<JobProgress>? progress = null)
    {
        await using var handle = await lease.TryAcquireAsync("enrichment_imdb", cancellationToken);
        if (handle is null)
        {
            logger.LogWarning("Another writer holds the lease; Enrichment --imdb refused.");
            return new ImdbEnrichmentReport(Refused: true, 0, 0, 0);
        }

        var startedAt = DateTimeOffset.UtcNow;
        var run = new JobRun { Id = idGenerator.NextId(), JobName = "Enrichment.Imdb", StartedAt = startedAt };
        db.JobRuns.Add(run);
        await db.SaveChangesAsync(cancellationToken);

        // Only actively-tracked shows that carry an imdb_id have anything to reconcile (ADR-021/ADR-031).
        var targets = (await trackedShows.ListActiveAsync(cancellationToken))
            .Where(s => s.ImdbId is not null)
            .ToList();

        var renewInterval = TimeSpan.FromSeconds(options.Sync.LeaseRenewIntervalSeconds);
        var lastRenew = DateTimeOffset.UtcNow;
        var changed = 0;
        var mismatchTotal = 0;
        var index = 0;

        foreach (var show in targets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            index++;

            // Progress at Information so a run is legible in the log file (see the Promoter's per-show line).
            logger.LogInformation(
                "EnrichmentImdb {Index}/{Total}: tvmaze {TvmazeId} (imdb {ImdbId}).",
                index, targets.Count, show.TvmazeId, show.ImdbId);

            // Surface the same step to an in-process caller (the Manage page); null from the console.
            progress?.Report(new JobProgress("Enrichment --imdb", index, targets.Count, $"tvmaze {show.TvmazeId}"));

            if (DateTimeOffset.UtcNow - lastRenew >= renewInterval)
            {
                if (!await handle.RenewAsync(cancellationToken))
                {
                    logger.LogError("Lost the enrichment_imdb lease after {Changed} changes; stopping.", changed);
                    break;
                }

                lastRenew = DateTimeOffset.UtcNow;
            }

            // Each show is its own unit of work: the mapper stages the gap-fills and the wiped/recomputed
            // mismatch rows, and the job commits them here. A show with no imdb_episode rows is a no-op.
            var result = await mapper.EnrichFromImdbAsync(show.Id, cancellationToken);
            await db.SaveChangesAsync(cancellationToken);

            if (result.Changed)
            {
                changed++;
            }

            mismatchTotal += result.Mismatches;
        }

        run.FinishedAt = DateTimeOffset.UtcNow;
        run.Succeeded = true;
        run.ItemsTotal = targets.Count;
        run.ItemsChanged = changed;
        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Enrichment --imdb done: {Total} shows with an imdb_id, {Changed} filled a gap, {Mismatch} mismatches recorded.",
            targets.Count, changed, mismatchTotal);

        return new ImdbEnrichmentReport(Refused: false, targets.Count, changed, mismatchTotal);
    }
}

/// <summary>What one <c>--imdb</c> run did. <see cref="Refused"/> means another writer held the lease.</summary>
internal sealed record ImdbEnrichmentReport(bool Refused, int Total, int Changed, int Mismatches);
