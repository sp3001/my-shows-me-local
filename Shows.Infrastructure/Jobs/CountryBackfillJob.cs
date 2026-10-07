using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Shows.Application;
using Shows.Infrastructure.Configuration;
using Shows.Infrastructure.Persistence;

namespace Shows.Infrastructure.Jobs;

/// <summary>
/// The <c>Enrichment --country</c> job body (improvement 21): re-derive every actively-tracked show's
/// country out of the TVmaze payload <b>already stored</b> in <c>tvmaze_show.raw_json</c>. It makes a
/// newly added derived column populate for the existing library without a single network request.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a job at all.</b> <c>tracked_show.country</c> is a mapped column added after the library was
/// imported, so all 547 existing rows are null. The refresh jobs would fix them only as a side effect of
/// re-fetching (547 requests, capped per run), and would re-base every other field to whatever TVmaze
/// says today. <c>raw_json</c> exists precisely so a re-map does not need the provider (SCHEMA §3), so
/// this job reads the database and writes through the mapper.
/// </para>
/// <para>
/// <b>No transport, so no R14 path.</b> Nothing here can be unreachable: unlike <c>--akas</c>, there is no
/// per-show fetch to fail, and unlike <c>--imdb</c>, there is no external dataset to be missing. A run
/// either re-maps what is on disk or reports what it could not parse (the mapper logs those).
/// </para>
/// <para>
/// <b>Idempotence:</b> the mapper's writes are all guarded comparisons, so a second run over the same
/// payloads reports <c>items_changed = 0</c>. That is what makes it safe to leave this mode in the
/// Enrichment job permanently rather than as a one-off script.
/// </para>
/// <para>
/// <b>Lease-guarded</b> (ADR-028) as <c>enrichment_country</c> with a heartbeat; a renewal that updates
/// zero rows stops the run. Selection is the shared repository query, so <c>deleted_at IS NULL</c> holds
/// (R8) — a soft-deleted show is never rewritten.
/// </para>
/// </remarks>
internal sealed class CountryBackfillJob(
    ShowsDbContext db,
    ITrackedShowRepository trackedShows,
    TvmazeShowMapper mapper,
    IWriterLease lease,
    IIdGenerator idGenerator,
    ShowsOptions options,
    ILogger<CountryBackfillJob> logger)
{
    // progress is optional and LAST so the console/test call RunAsync(ct) still binds; only the
    // in-process Manage path passes it.
    public async Task<CountryBackfillReport> RunAsync(
        CancellationToken cancellationToken, IProgress<JobProgress>? progress = null)
    {
        await using var handle = await lease.TryAcquireAsync("enrichment_country", cancellationToken);
        if (handle is null)
        {
            logger.LogWarning("Another writer holds the lease; Enrichment --country refused.");
            return new CountryBackfillReport(Refused: true, 0, 0);
        }

        var startedAt = DateTimeOffset.UtcNow;
        // job_run.id is client-minted (ValueGenerated.Never, ADR-026/027).
        var run = new JobRun { Id = idGenerator.NextId(), JobName = "Enrichment.Country", StartedAt = startedAt };
        db.JobRuns.Add(run);
        await db.SaveChangesAsync(cancellationToken);

        var targets = await trackedShows.ListActiveAsync(cancellationToken);
        var renewInterval = TimeSpan.FromSeconds(options.Sync.LeaseRenewIntervalSeconds);
        var lastRenew = DateTimeOffset.UtcNow;

        // Bounded batches: the mapper reads raw_json for every show in a batch, and a batch is the unit of
        // work — commit per batch, renew the lease between them (the ImdbImporter lesson: a heartbeat on a
        // separate connection cannot coexist with one long write transaction).
        const int batchSize = 100;
        var changed = 0;
        var index = 0;

        foreach (var batch in targets.Chunk(batchSize))
        {
            cancellationToken.ThrowIfCancellationRequested();
            index += batch.Length;

            logger.LogInformation("EnrichmentCountry {Index}/{Total}: tvmaze batch.", index, targets.Count);
            progress?.Report(new JobProgress("Enrichment --country", index, targets.Count, $"{batch.Length} shows"));

            if (DateTimeOffset.UtcNow - lastRenew >= renewInterval)
            {
                if (!await handle.RenewAsync(cancellationToken))
                {
                    logger.LogError("Lost the enrichment_country lease after {Changed} changes; stopping.", changed);
                    break;
                }

                lastRenew = DateTimeOffset.UtcNow;
            }

            var batchChanged = await mapper.RemapCountryFromStoredPayloadAsync(
                batch.Select(s => s.Id).ToList(), cancellationToken);
            await db.SaveChangesAsync(cancellationToken);

            changed += batchChanged;
        }

        run.FinishedAt = DateTimeOffset.UtcNow;
        run.Succeeded = true;
        run.ItemsTotal = targets.Count;
        run.ItemsChanged = changed;
        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Enrichment --country done: {Total} shows re-mapped from stored payloads, {Changed} countries set.",
            targets.Count, changed);

        return new CountryBackfillReport(Refused: false, targets.Count, changed);
    }
}

/// <summary>
/// What one <c>--country</c> run did. <see cref="Refused"/> means another writer held the lease.
/// <see cref="Changed"/> counts shows whose <b>mapped</b> country moved — source-column churn is not a
/// change (the same honesty as <c>--akas</c>), which is what makes a re-run report 0.
/// </summary>
internal sealed record CountryBackfillReport(bool Refused, int Total, int Changed);
