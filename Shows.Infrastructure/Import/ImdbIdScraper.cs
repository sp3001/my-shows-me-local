using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Shows.Application;
using Shows.Infrastructure.Configuration;
using Shows.Infrastructure.Persistence;
using Shows.Infrastructure.Persistence.Staging;

namespace Shows.Infrastructure.Import;

/// <summary>
/// The <c>ScrapeMyShows</c> job body (step 2.2): for each staged show, fetch its myshows page and record
/// the IMDb (and Kinopoisk) id in <c>staging_imdb_id</c>, then mirror the IMDb id onto
/// <c>staging_show.imdb_id</c> so <c>v_staging_issues</c> and the TVmaze lookup (step 2.3) can read it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Resumable:</b> a show already carrying a resolved <c>imdb_id</c> is skipped; a row with
/// <c>imdb_id IS NULL AND error IS NOT NULL</c> is retried (SCHEMA.md §5). <b>Capped:</b> at most
/// <c>MyShows.MaxRequestsPerRun</c> requests per run (0 = uncapped), so an interrupted run can be
/// resumed without a single invocation hammering the site. <b>Failures are data, not exceptions:</b> a
/// transport failure or a non-200 is written as <c>http_status</c> + <c>error</c>, never thrown, so one
/// bad page does not abort the run — and a transport failure stays distinguishable from "page has no
/// IMDb id" (SPEC R14).
/// </para>
/// <para>
/// <b>Lease-guarded:</b> this is a bulk multi-transaction writer, so it takes the <c>scrape_myshows</c>
/// lease and renews on the heartbeat; a renewal that returns false means the lease was lost and the run
/// stops writing immediately (ADR-028).
/// </para>
/// </remarks>
internal sealed class ImdbIdScraper(
    ShowsDbContext db,
    IMyShowsPageFetcher fetcher,
    IWriterLease lease,
    ShowsOptions options,
    ILogger<ImdbIdScraper> logger)
{
    private const string LeaseName = "scrape_myshows";

    // progress is optional and LAST so the existing console/test call RunAsync(ct) still binds; only the
    // in-process Manage path passes it.
    public async Task<ScrapeReport> RunAsync(CancellationToken cancellationToken, IProgress<JobProgress>? progress = null)
    {
        await using var handle = await lease.TryAcquireAsync(LeaseName, cancellationToken);
        if (handle is null)
        {
            logger.LogWarning("Another writer holds the lease; scrape refused.");
            return new ScrapeReport(Refused: true, 0, 0, 0, 0, 0);
        }

        // Targets: every staged show without a resolved imdb_id yet. A prior failed attempt (error set,
        // imdb_id null) is included so it is retried; a resolved row is skipped.
        var targets = await db.StagingShows
            .Where(s => !db.StagingImdbIds.Any(i => i.MyshowsId == s.MyshowsId && i.ImdbId != null))
            .OrderBy(s => s.MyshowsId)
            .Select(s => s.MyshowsId)
            .ToListAsync(cancellationToken);

        var alreadyResolved = await db.StagingImdbIds.CountAsync(i => i.ImdbId != null, cancellationToken);

        var cap = options.MyShows.MaxRequestsPerRun;
        if (cap > 0 && targets.Count > cap)
        {
            logger.LogInformation(
                "{Total} shows to scrape, capped at {Cap} this run ({Remaining} left for the next).",
                targets.Count, cap, targets.Count - cap);
            targets = targets.Take(cap).ToList();
        }

        var renewInterval = TimeSpan.FromSeconds(options.Sync.LeaseRenewIntervalSeconds);
        var lastRenew = DateTimeOffset.UtcNow;
        var attempted = 0;
        var resolved = 0;
        var noId = 0;
        var failed = 0;

        foreach (var myshowsId in targets)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Heartbeat: renew before each request once the interval has elapsed. Zero rows updated means
            // the lease is gone — stop writing now, do not finish the run (ADR-028).
            if (DateTimeOffset.UtcNow - lastRenew >= renewInterval)
            {
                if (!await handle.RenewAsync(cancellationToken))
                {
                    logger.LogError("Lost the {Lease} lease after {Attempted} requests; stopping.", LeaseName, attempted);
                    break;
                }

                lastRenew = DateTimeOffset.UtcNow;
            }

            attempted++;
            var fetch = await fetcher.FetchAsync(myshowsId, cancellationToken);
            var outcome = await RecordAsync(myshowsId, fetch, cancellationToken);

            // Progress at Information so a run is legible in the log file: which show, how far through the
            // batch, and how it resolved (see the Promoter's per-show line).
            logger.LogInformation(
                "ScrapeMyShows {Index}/{Total}: myshows {MyshowsId} ({Outcome}).",
                attempted, targets.Count, myshowsId, outcome);

            // Surface the same step to an in-process caller (the Manage page); null from the console.
            progress?.Report(new JobProgress("ScrapeMyShows", attempted, targets.Count, $"myshows {myshowsId}"));

            switch (outcome)
            {
                case ScrapeOutcome.Resolved:
                    resolved++;
                    break;
                case ScrapeOutcome.NoId:
                    noId++;
                    break;
                case ScrapeOutcome.Failed:
                    failed++;
                    break;
            }
        }

        var report = new ScrapeReport(false, attempted, resolved, noId, failed, alreadyResolved);
        logger.LogInformation(
            "Scrape done: {Attempted} requested, {Resolved} resolved, {NoId} no-id, {Failed} failed "
            + "({AlreadyResolved} were already resolved before this run).",
            report.Attempted, report.Resolved, report.NoId, report.Failed, report.AlreadyResolved);
        return report;
    }

    private async Task<ScrapeOutcome> RecordAsync(int myshowsId, PageFetchResult fetch, CancellationToken ct)
    {
        string? imdbId = null;
        string? kinopoiskId = null;
        string? error = fetch.Error;
        var outcome = ScrapeOutcome.Failed;

        if (fetch.Html is not null)
        {
            var (parsedImdb, parsedKino, ambiguous) = MyShowsPageParser.Parse(fetch.Html);
            kinopoiskId = parsedKino;
            if (ambiguous)
            {
                // More than one distinct IMDb id: the markup changed. Record it as a failure rather than
                // pick one (step 2.2: no silent fallback).
                error = "ambiguous imdb ids on page";
            }
            else if (parsedImdb is not null)
            {
                imdbId = parsedImdb;
                outcome = ScrapeOutcome.Resolved;
            }
            else
            {
                error = "no imdb id on page";
                outcome = ScrapeOutcome.NoId;
            }
        }

        var row = await db.StagingImdbIds.FindAsync([myshowsId], ct);
        if (row is null)
        {
            row = new StagingImdbId { MyshowsId = myshowsId };
            db.StagingImdbIds.Add(row);
        }

        row.ImdbId = imdbId;
        row.KinopoiskId = kinopoiskId;
        row.HttpStatus = fetch.HttpStatus;
        row.Error = error;
        row.ScrapedAt = DateTimeOffset.UtcNow;

        // Mirror onto the denormalized copy the view and the TVmaze lookup read (SCHEMA.md §5). Only set
        // it when we actually learned an id — never clear a previously-good one on a transient failure.
        if (imdbId is not null)
        {
            await db.StagingShows
                .Where(s => s.MyshowsId == myshowsId)
                .ExecuteUpdateAsync(u => u.SetProperty(s => s.ImdbId, imdbId), ct);
        }

        await db.SaveChangesAsync(ct);
        return outcome;
    }
}

/// <summary>How one scraped page resolved, so the run's counters match what was actually recorded.</summary>
internal enum ScrapeOutcome
{
    Resolved,
    NoId,
    Failed,
}

/// <summary>What one scrape run did. <see cref="Refused"/> means another writer held the lease.</summary>
internal sealed record ScrapeReport(
    bool Refused,
    int Attempted,
    int Resolved,
    int NoId,
    int Failed,
    int AlreadyResolved);
