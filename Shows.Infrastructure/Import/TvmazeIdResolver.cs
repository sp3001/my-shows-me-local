using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Shows.Application;
using Shows.Infrastructure.Configuration;
using Shows.Infrastructure.Persistence;
using Shows.Infrastructure.Tvmaze;

namespace Shows.Infrastructure.Import;

/// <summary>
/// The <c>--resolve</c> job body (step 2.3): for each staged show that has an IMDb id but no TVmaze id
/// yet, call TVmaze <c>/lookup/shows?imdb=</c> and record the result in <c>staging_show.tvmaze_id</c> /
/// <c>tvmaze_resolution</c>. This is the bridge from the scraped IMDb ids to the TVmaze ids the mapper
/// needs, and it is what clears the <c>unresolved_tvmaze_id</c> blocking rows out of
/// <c>v_staging_issues</c> (SCHEMA.md §6).
/// </summary>
/// <remarks>
/// <para>
/// <b>Resumable:</b> a show that already carries a <c>tvmaze_id</c> is skipped — which is also what
/// protects the user's <b>manual</b> <c>tvmaze_id</c> on show 96144 (it is never a target, so it is
/// never overwritten). A show with an <c>imdb_id</c> and a null <c>tvmaze_id</c> is (re)tried, so a
/// genuine 404 from a prior run is revisited — TVmaze adds shows, and a transient miss may resolve later.
/// </para>
/// <para>
/// <b>R14 is the load-bearing distinction here.</b> The provider returns <c>null</c> for a genuine 404
/// ("TVmaze has no show with that IMDb id") — that is recorded as <c>unresolved</c>. A transport failure
/// throws <see cref="TvmazeUnavailableException"/> ("we never reached TVmaze") — that is NOT recorded as
/// unresolved; the row is left untouched for the next run and counted as failed. Marking a transport
/// failure unresolved would make a dead proxy look like "TVmaze has no data" and silently drop shows from
/// tracking — the exact failure R14 exists to prevent.
/// </para>
/// <para>
/// <b>Capped</b> at <c>Tvmaze.MaxRequestsPerRun</c> per run (0 = uncapped), lowest <c>myshows_id</c>
/// first, and <b>lease-guarded</b> under <c>resolve_tvmaze</c> with a heartbeat — a renewal that returns
/// false stops writing immediately (ADR-028). The provider's throttle (250 ms) already serialises the
/// requests; the cap bounds a single invocation, not the rate.
/// </para>
/// </remarks>
internal sealed class TvmazeIdResolver(
    ShowsDbContext db,
    ICatalogProvider provider,
    IWriterLease lease,
    ShowsOptions options,
    ILogger<TvmazeIdResolver> logger)
{
    private const string LeaseName = "resolve_tvmaze";

    // progress is optional and LAST so the existing console/test call RunAsync(ct) still binds; only the
    // in-process Manage path passes it.
    public async Task<ResolveReport> RunAsync(CancellationToken cancellationToken, IProgress<JobProgress>? progress = null)
    {
        await using var handle = await lease.TryAcquireAsync(LeaseName, cancellationToken);
        if (handle is null)
        {
            logger.LogWarning("Another writer holds the lease; resolve refused.");
            return new ResolveReport(Refused: true, 0, 0, 0, 0, 0);
        }

        // Targets: a staged show with an IMDb id but no TVmaze id yet. A show that already has a
        // tvmaze_id — scraped OR manual — is excluded, so the manual id on 96144 is never a target.
        var targets = await db.StagingShows
            .Where(s => s.ImdbId != null && s.TvmazeId == null)
            .OrderBy(s => s.MyshowsId)
            .Select(s => new { s.MyshowsId, ImdbId = s.ImdbId! })
            .ToListAsync(cancellationToken);

        var alreadyResolved = await db.StagingShows.CountAsync(s => s.TvmazeId != null, cancellationToken);

        var cap = options.Tvmaze.MaxRequestsPerRun;
        if (cap > 0 && targets.Count > cap)
        {
            logger.LogInformation(
                "{Total} shows to resolve, capped at {Cap} this run ({Remaining} left for the next).",
                targets.Count, cap, targets.Count - cap);
            targets = targets.Take(cap).ToList();
        }

        var renewInterval = TimeSpan.FromSeconds(options.Sync.LeaseRenewIntervalSeconds);
        var lastRenew = DateTimeOffset.UtcNow;
        var attempted = 0;
        var resolved = 0;
        var unresolved = 0;
        var failed = 0;

        foreach (var target in targets)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Heartbeat: renew before each request once the interval has elapsed. Zero rows updated means
            // the lease is gone — stop writing now, do not finish the run (ADR-028).
            if (DateTimeOffset.UtcNow - lastRenew >= renewInterval)
            {
                if (!await handle.RenewAsync(cancellationToken))
                {
                    logger.LogError("Lost the {Lease} lease after {Attempted} lookups; stopping.", LeaseName, attempted);
                    break;
                }

                lastRenew = DateTimeOffset.UtcNow;
            }

            attempted++;
            var outcome = await RecordAsync(target.MyshowsId, target.ImdbId, cancellationToken);

            // Progress at Information so a run is legible in the log file: which show, how far through the
            // batch, and how it resolved (see the Promoter's per-show line).
            logger.LogInformation(
                "ResolveTvmaze {Index}/{Total}: myshows {MyshowsId} (imdb {ImdbId}, {Outcome}).",
                attempted, targets.Count, target.MyshowsId, target.ImdbId, outcome);

            // Surface the same step to an in-process caller (the Manage page); null from the console.
            progress?.Report(new JobProgress("Import --resolve", attempted, targets.Count, $"myshows {target.MyshowsId}"));

            switch (outcome)
            {
                case ResolveOutcome.Resolved:
                    resolved++;
                    break;
                case ResolveOutcome.NotFound:
                    unresolved++;
                    break;
                case ResolveOutcome.Failed:
                    failed++;
                    break;
            }
        }

        var report = new ResolveReport(false, attempted, resolved, unresolved, failed, alreadyResolved);
        logger.LogInformation(
            "Resolve done: {Attempted} looked up, {Resolved} resolved, {Unresolved} not on TVmaze, "
            + "{Failed} transport-failed ({AlreadyResolved} already had a tvmaze_id before this run).",
            report.Attempted, report.Resolved, report.Unresolved, report.Failed, report.AlreadyResolved);
        return report;
    }

    private async Task<ResolveOutcome> RecordAsync(int myshowsId, string imdbId, CancellationToken ct)
    {
        int? tvmazeId;
        try
        {
            tvmazeId = await provider.LookupByImdbIdAsync(imdbId, ct);
        }
        catch (TvmazeUnavailableException ex)
        {
            // R14: we never reached TVmaze. Leave the row exactly as it was (tvmaze_id NULL, resolution
            // 'unresolved') so the next run retries it. Do NOT record this as a genuine "not on TVmaze".
            logger.LogWarning(
                ex, "TVmaze unreachable for show {MyshowsId} (imdb {ImdbId}); leaving for the next run.",
                myshowsId, imdbId);
            return ResolveOutcome.Failed;
        }

        // Only touch the row when we learned something definitive (a hit or a real 404). A transport
        // failure never reaches here. The resolution string is computed outside the expression tree —
        // an `is null` pattern is not translatable there.
        var resolution = tvmazeId is null ? "unresolved" : "scraped";
        await db.StagingShows
            .Where(s => s.MyshowsId == myshowsId)
            .ExecuteUpdateAsync(u => u
                .SetProperty(s => s.TvmazeId, tvmazeId)
                .SetProperty(s => s.TvmazeResolution, resolution), ct);

        return tvmazeId is null ? ResolveOutcome.NotFound : ResolveOutcome.Resolved;
    }
}

/// <summary>How one lookup resolved, so the run's counters match what was actually recorded.</summary>
internal enum ResolveOutcome
{
    Resolved,
    NotFound,
    Failed,
}

/// <summary>What one resolve run did. <see cref="Refused"/> means another writer held the lease.</summary>
internal sealed record ResolveReport(
    bool Refused,
    int Attempted,
    int Resolved,
    int Unresolved,
    int Failed,
    int AlreadyResolved);
