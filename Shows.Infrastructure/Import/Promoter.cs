using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Shows.Application;
using Shows.Domain;
using Shows.Infrastructure.Configuration;
using Shows.Infrastructure.Persistence;
using Shows.Infrastructure.Persistence.Sources;
using Shows.Infrastructure.Persistence.Staging;
using Shows.Infrastructure.Tvmaze;

namespace Shows.Infrastructure.Import;

/// <summary>
/// The <c>--promote</c> job body (step 2.6): the one-way move from the reviewed <c>staging_*</c> tables
/// into the mapped and user tables. It is the sole writer of <c>user_show</c> and <c>episode_watch</c>
/// during the import (SPEC P4 — those rows are otherwise only touched by the UI), and it is the only
/// path that creates a <c>tracked_show</c> for a whole library at once.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two-phase import, second phase (AGENTS.md).</b> The user has already reviewed <c>staging_show</c>
/// in DB Browser (step 2.5); <c>--promote</c> reads that reviewed state and never writes a staging
/// column except <c>promoted_at</c>. It refuses outright while <c>v_staging_issues</c> still reports a
/// <c>blocking</c> row (SCHEMA §6) — a blocking row means the review is not finished.
/// </para>
/// <para>
/// <b>Exclusive of every other writer (ADR-028).</b> <c>--promote</c> takes the <c>promote</c> lease and
/// additionally refuses if any <em>foreign</em> lease is live: it is the one operation that must not
/// overlap another bulk job, because it writes the irreplaceable user tables. The foreign-lease probe is
/// raw SQL — EF Core's SQLite provider cannot translate a <c>DateTimeOffset</c> comparison in a LINQ
/// predicate at all, so liveness is checked where the provider can't (the same TEXT-lexicographic
/// comparison the lease's own acquire uses).
/// </para>
/// <para>
/// <b>Per-show transaction, never resume.</b> Each show is one transaction: fetch (outside it, so the
/// network never holds a write lock), then create the rows, map, backfill watch marks, and stamp
/// <c>promoted_at</c>. A failure rolls the whole show back and leaves <c>promoted_at</c> null, so the
/// next run re-does exactly that show with fresh ids (AGENTS.md: never resume a failed transaction).
/// Idempotence across runs is <c>promoted_at IS NULL</c> — a promoted show is never a target again, so
/// its episode ids are never re-minted and its watch marks are never touched.
/// </para>
/// <para>
/// <b>The mapper runs before the watch backfill, deliberately.</b> The mapper never writes
/// <c>episode_watch</c> (SPEC P4); it computes the cached columns against an empty watch set on the
/// first map. The backfill then writes the marks and recomputes the cached columns through the one
/// Domain function (<see cref="ShowProgress"/>), so the single definition of progress still owns the
/// final numbers (ARCHITECTURE.md §5).
/// </para>
/// </remarks>
internal sealed class Promoter(
    ShowsDbContext db,
    TvmazeShowMapper mapper,
    ITvmazeShowSource source,
    IIdGenerator idGenerator,
    IWriterLease lease,
    ShowsOptions options,
    ILogger<Promoter> logger)
{
    private const string LeaseName = "promote";

    // progress is optional and LAST so the existing console/test call RunAsync(ct) still binds; only the
    // in-process Manage path passes it.
    public async Task<PromoteReport> RunAsync(CancellationToken cancellationToken, IProgress<JobProgress>? progress = null)
    {
        await using var handle = await lease.TryAcquireAsync(LeaseName, cancellationToken);
        if (handle is null)
        {
            logger.LogWarning("Another writer holds the {Lease} lease; promote refused.", LeaseName);
            return PromoteReport.RefusedResult("the promote lease is held by another process");
        }

        // Exclusive of every other lease (ADR-028): a live foreign lease means another bulk job is
        // writing while we are about to write the irreplaceable user tables. Raw SQL because the SQLite
        // provider cannot translate a DateTimeOffset comparison in LINQ; the TEXT comparison matches the
        // lease's own acquire (SqliteWriterLease), which stores and compares these instants as ISO-8601.
        // COUNT(*) boxes as Int64 through Microsoft.Data.Sqlite, hence the long in SqlQueryRaw.
        var foreignLive = await db.Database
            .SqlQueryRaw<long>(
                "SELECT COUNT(*) AS [Value] FROM writer_lease WHERE expires_at > {0} AND name <> '"
                + LeaseName + "'",
                new object[] { DateTimeOffset.UtcNow })
            .SingleAsync(cancellationToken);
        if (foreignLive > 0)
        {
            logger.LogWarning(
                "Promote refused: {Count} foreign writer lease(s) are live. --promote is exclusive of "
                + "every other bulk job (ADR-028); stop them and retry.", foreignLive);
            return PromoteReport.RefusedResult($"{foreignLive} foreign writer lease(s) are live");
        }

        // The review gate (SCHEMA §6): any blocking row means the human review is unfinished.
        var blocking = await db.StagingIssues.CountAsync(
            i => i.Severity == "blocking", cancellationToken);
        if (blocking > 0)
        {
            logger.LogError(
                "Promote refused: {Count} blocking row(s) in v_staging_issues. Resolve them in DB "
                + "Browser (step 2.5) before promoting.", blocking);
            return PromoteReport.RefusedResult($"{blocking} blocking issue(s) remain");
        }

        // Targets: reviewed, tvmaze-resolved, not yet promoted. A show with no tvmaze_id can never be
        // promoted (the mapper has nothing to map) and is surfaced by the gate as blocking, so it is not
        // a target here.
        var targets = await db.StagingShows
            .Where(s => s.PromotedAt == null && s.TvmazeId != null)
            .OrderBy(s => s.MyshowsId)
            .Select(s => new PromoteTarget(
                s.MyshowsId, s.TvmazeId!.Value, s.SourceWatchStatus, s.TotalEpisodes, s.WatchedEpisodes))
            .ToListAsync(cancellationToken);

        var renewInterval = TimeSpan.FromSeconds(options.Sync.LeaseRenewIntervalSeconds);
        var lastRenew = DateTimeOffset.UtcNow;
        var promoted = 0;
        var failed = 0;
        var index = 0;

        foreach (var target in targets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            index++;

            // Progress at Information so a run is legible in the log file: which show, and how far
            // through the batch. 547 shows over the network is slow enough that silence looks like a hang.
            logger.LogInformation(
                "Promote {Index}/{Total}: myshows {MyshowsId} (tvmaze {TvmazeId}, {Status}).",
                index, targets.Count, target.MyshowsId, target.TvmazeId, target.SourceWatchStatus);

            // Surface the same step to an in-process caller (the Manage page); null from the console.
            progress?.Report(new JobProgress("Import --promote", index, targets.Count, $"myshows {target.MyshowsId}"));

            if (DateTimeOffset.UtcNow - lastRenew >= renewInterval)
            {
                if (!await handle.RenewAsync(cancellationToken))
                {
                    logger.LogError(
                        "Lost the {Lease} lease after {Promoted} shows; stopping. Re-run to resume at the "
                        + "first un-promoted show.", LeaseName, promoted);
                    break;
                }

                lastRenew = DateTimeOffset.UtcNow;
            }

            // Fetch OUTSIDE the transaction: a network round-trip must not hold a SQLite write lock, and
            // a transport failure (R14) leaves the show untouched for the next run.
            TvmazePayload? payload;
            try
            {
                payload = await source.FetchAsync(target.TvmazeId, cancellationToken);
            }
            catch (TvmazeUnavailableException ex)
            {
                logger.LogWarning(ex, "TVmaze unreachable for show {MyshowsId}; leaving un-promoted.",
                    target.MyshowsId);
                failed++;
                continue;
            }

            if (payload is null)
            {
                logger.LogWarning("TVmaze has no show {TvmazeId} (myshows {MyshowsId}); cannot promote.",
                    target.TvmazeId, target.MyshowsId);
                failed++;
                continue;
            }

            try
            {
                await PromoteOneAsync(target, payload, cancellationToken);
                promoted++;
            }
            catch (Exception ex)
            {
                // The transaction rolled back; promoted_at is still null, so the next run retries this
                // show with fresh ids. Do not abort the whole run for one bad show.
                logger.LogError(ex, "Promote failed for myshows {MyshowsId} (tvmaze {TvmazeId}); "
                    + "rolled back, will retry next run.", target.MyshowsId, target.TvmazeId);
                failed++;
            }
            finally
            {
                // One context serves every show, so its change tracker must be emptied between them.
                // After a commit the entities are Unchanged (harmless to SaveChanges but they would
                // accumulate across 547 shows); after a rollback they are still Added/Modified and would
                // be re-submitted by the NEXT show's SaveChanges — leaking a failed show's rows into an
                // unrelated one. Clearing on both paths keeps each show's unit of work isolated.
                db.ChangeTracker.Clear();
            }
        }

        var report = new PromoteReport(false, null, promoted, failed, targets.Count);
        logger.LogInformation(
            "Promote done: {Promoted} promoted, {Failed} failed, {Total} targeted.",
            promoted, failed, targets.Count);
        return report;
    }

    // One show, one transaction. The mapper is the sole writer of source + mapped rows (SPEC R11); this
    // method is the sole writer of tracked_show, user_show and episode_watch during the import.
    private async Task PromoteOneAsync(
        PromoteTarget target, TvmazePayload payload, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        // Reuse an existing tracked_show if the user already added this show through the UI — promote
        // must not collide with the UNIQUE tvmaze_id, and the existing row's ids are already pointed at
        // by any watch marks. (The real DB is empty here; this is the guard that makes a re-run or an
        // interleaved UI add safe rather than a crash.)
        var show = await db.TrackedShows
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(s => s.TvmazeId == target.TvmazeId, ct);

        var now = DateTimeOffset.UtcNow;

        if (show is null)
        {
            await mapper.EnsureSourceRowAsync(target.TvmazeId, ct);

            show = new TrackedShow { Id = idGenerator.NextId(), TvmazeId = target.TvmazeId };
            db.TrackedShows.Add(show);

            // The shadow FK to the source shell, set by property-name STRING so this type never names the
            // Sources entity (SPEC P3, the same discipline as TrackedShowRepository).
            db.Entry(show).Property<int>("tvmaze_show_id").CurrentValue = target.TvmazeId;
        }

        var category = MapCategory(target.SourceWatchStatus);

        await db.SaveChangesAsync(ct);

        // Map the real payload over the shell: source rows, seasons, episodes, genres, status. The mapper
        // computes the cached columns against an empty watch set here — the backfill recomputes them next.
        await mapper.MapShowAsync(payload, ct);
        await db.SaveChangesAsync(ct);

        // Backfill the watch marks from the reviewed staging data, then recompute progress through the
        // one Domain function so the final cached columns reflect the marks the mapper could not see.
        var needsReview = await BackfillWatchMarksAsync(show, target, ct);

        // The user_show row is the irreplaceable decision (SPEC P4), written AFTER the backfill so
        // needs_review is authored in the same insert rather than patched afterwards. It is authored once,
        // at import, and cleared by the user in the UI. If the show was already tracked (a UI add, or a
        // re-run that rolled back before this line), the row exists and only needs_review is reconciled —
        // category and added_at are the user's and are never reset here.
        var userShow = await db.UserShows
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(u => u.TrackedShowId == show.Id, ct);

        if (userShow is null)
        {
            db.UserShows.Add(new UserShow
            {
                TrackedShowId = show.Id,
                Category = category,
                AddedAt = now,
                CategoryChangedAt = now,
                NeedsReview = needsReview,
            });
        }
        else if (userShow.NeedsReview != needsReview)
        {
            userShow.NeedsReview = needsReview;
        }

        await db.SaveChangesAsync(ct);

        await db.StagingShows
            .Where(s => s.MyshowsId == target.MyshowsId)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.PromotedAt, now), ct);

        await tx.CommitAsync(ct);
    }

    // The four-branch backfill (DATA_ANALYSIS §5). Returns needs_review. Specials are never marked in
    // any branch. The mapped Episode rows already exist (the mapper ran), so the plan is expressed
    // against them directly — episode_watch points at their ids.
    private async Task<bool> BackfillWatchMarksAsync(
        TrackedShow show, PromoteTarget target, CancellationToken ct)
    {
        var episodes = await db.Episodes
            .Where(e => e.ShowId == show.Id)
            .ToListAsync(ct);

        // Absolute (season, episode) order. EpisodeNumber is null only for IMDb-legacy rows, which never
        // appear in a TVmaze-mapped import; sort them last so they are never "before" a real episode.
        var ordered = episodes
            .OrderBy(e => e.SeasonNumber)
            .ThenBy(e => e.EpisodeNumber ?? int.MaxValue)
            .ToList();
        // The set that counts toward progress: regulars and significant specials (SPEC R4 / ADR-034).
        // Insignificant specials and Unknown are excluded here, so they are never auto-marked and can
        // never set the watched frontier — the season-0 filler special that used to zero out the frontier
        // is now EpisodeType.InsignificantSpecial and drops out of this list entirely.
        var regulars = ordered
            .Where(e => e.EpisodeType is EpisodeType.Regular or EpisodeType.Special)
            .ToList();

        // The staging list is the UNWATCHED set (SCHEMA §5). Match on (season, episode) against the
        // mapped catalog; entries absent from the catalog are ignored (DATA_ANALYSIS §5 branch 1).
        var unwatchedList = (await db.StagingEpisodes
            .Where(e => e.ShowMyshowsId == target.MyshowsId)
            .Select(e => new SeasonEpisode(e.SeasonNumber, e.EpisodeNumber))
            .ToListAsync(ct)).ToHashSet();

        List<Episode> toMark;
        bool needsReview;

        if (unwatchedList.Count > 0)
        {
            // Branch 1: explicit list. The boundary is the earliest unwatched episode in the counted set
            // (regulars + significant specials, ADR-034). The frontier is computed over that set only —
            // insignificant specials are never in the watched frontier (SPEC R4, DATA_ANALYSIS §5), and
            // now that ?specials=1 puts season-0 filler specials in the catalog, intersecting over ALL
            // episodes would let one set the boundary to season 0 and mark nothing. So match the list
            // against the counted set, then mark every counted episode before it.
            var intersection = regulars
                .Where(e => unwatchedList.Contains(new SeasonEpisode(e.SeasonNumber, e.EpisodeNumber ?? 0)))
                .ToList();
            (int Season, int Episode)? boundary = intersection.Count == 0
                ? null
                : (intersection[0].SeasonNumber, intersection[0].EpisodeNumber ?? 0);

            toMark = boundary is null
                ? []
                : regulars.Where(e => Before(e, boundary.Value)).ToList();
            needsReview = toMark.Count != target.WatchedEpisodes;
        }
        else if (target.WatchedEpisodes == target.TotalEpisodes)
        {
            // Branch 3 (checked before the heuristic): fully watched is unambiguous — all regulars.
            // The distribution table (DATA_ANALYSIS §5) has 33 such shows and they must NOT be flagged
            // for review, which is why this precedes the watched>0 heuristic below.
            toMark = regulars;
            needsReview = false;
        }
        else if (target.WatchedEpisodes > 0)
        {
            // Branch 2: no list, partially watched — the heuristic. First N regulars, always reviewed.
            toMark = regulars.Take(target.WatchedEpisodes).ToList();
            needsReview = true;
        }
        else
        {
            // Branch 4: nothing watched (including the total=0 edge case → total_regular stays 0).
            toMark = [];
            needsReview = false;
        }

        var existingWatched = (await db.EpisodeWatches
            .Where(w => w.ShowId == show.Id)
            .Select(w => w.EpisodeId)
            .ToListAsync(ct)).ToHashSet();

        var now = DateTimeOffset.UtcNow;
        foreach (var episode in toMark)
        {
            if (existingWatched.Add(episode.Id))
            {
                db.EpisodeWatches.Add(new EpisodeWatch
                {
                    Id = idGenerator.NextId(),
                    ShowId = show.Id,
                    EpisodeId = episode.Id,
                    WatchedAt = now,
                });
            }
        }

        await db.SaveChangesAsync(ct);

        // Recompute the cached columns over the now-populated watch set (SPEC R5/R7 via the one function).
        var facts = episodes.Select(e =>
            new EpisodeProgressFact(e.EpisodeType, existingWatched.Contains(e.Id)));
        var progress = ShowProgress.Compute(show.Status, facts);
        show.WatchedRegular = progress.WatchedRegular;
        show.TotalRegular = progress.TotalRegular;
        show.HasUnwatchedSpecials = progress.HasUnwatchedSpecials;
        show.AutoFinishEligible = progress.AutoFinishEligible;
        await db.SaveChangesAsync(ct);

        return needsReview;
    }

    // Strict (season, episode) ordering: e sorts before the boundary when its season is lower, or the
    // same season with a lower episode number.
    private static bool Before(Episode e, (int Season, int Episode) boundary)
    {
        var ep = e.EpisodeNumber ?? int.MaxValue;
        return e.SeasonNumber < boundary.Season
            || (e.SeasonNumber == boundary.Season && ep < boundary.Episode);
    }

    // The four categories are exactly the four source files (AGENTS.md). An unrecognised value fails
    // closed rather than defaulting to a category — a show silently filed in the wrong tab is the
    // invisible failure ADR-024 exists to prevent.
    private static ShowCategory MapCategory(string sourceWatchStatus) => sourceWatchStatus switch
    {
        "watching" => ShowCategory.Started,
        "later" => ShowCategory.Later,
        "cancelled" => ShowCategory.Cancelled,
        "finished" => ShowCategory.Finished,
        _ => throw new InvalidOperationException(
            $"Unknown source_watch_status '{sourceWatchStatus}'; expected one of "
            + "watching|later|cancelled|finished."),
    };

    private readonly record struct PromoteTarget(
        int MyshowsId, int TvmazeId, string SourceWatchStatus, int TotalEpisodes, int WatchedEpisodes);

    private readonly record struct SeasonEpisode(int Season, int Episode);
}

/// <summary>
/// What one <c>--promote</c> run did. <see cref="Refused"/> means it wrote nothing — the lease was
/// held, a foreign lease was live, or the review gate still had a blocking row. A refused run is exit 1.
/// </summary>
internal sealed record PromoteReport(
    bool Refused,
    string? RefusedBecause,
    int Promoted,
    int Failed,
    int Targeted)
{
    public static PromoteReport RefusedResult(string because) => new(true, because, 0, 0, 0);
}
