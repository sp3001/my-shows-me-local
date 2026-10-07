using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Shows.Infrastructure.Configuration;
using Shows.Infrastructure.Persistence;
using Shows.Infrastructure.Persistence.Staging;

namespace Shows.Infrastructure.Import;

/// <summary>
/// The <c>--stage</c> / <c>--dry-run</c> / <c>--reset</c> behavior of the import job (step 2.1). Reads
/// the read-only myshows exports, parses them through <see cref="MyShowsEnvelopeParser"/>, and fills the
/// staging tables — never the mapped or user tables, which only the mapper and the user touch (SPEC P3/P4).
/// </summary>
/// <remarks>
/// <para>
/// Staging is <b>insert-if-missing</b> keyed on <c>myshows_id</c>, not truncate-and-reload: between
/// <c>--stage</c> and <c>--promote</c> the user edits these rows in DB Browser (step 2.5), and a re-run
/// must never clobber those edits. <see cref="ResetAsync"/> is the explicit escape hatch that clears the
/// staging tables this job owns (<c>staging_show</c>, <c>staging_episode</c>) — deliberately not
/// <c>staging_imdb_id</c>, which the scraper owns (step 2.2).
/// </para>
/// <para>
/// <see cref="DryRunAsync"/> opens no write transaction and never migrates; it only reads files and
/// reports, so it is safe to run against a database that does not exist yet.
/// </para>
/// </remarks>
internal sealed class StagingImporter(
    ShowsDbContext db,
    DataPaths paths,
    ILogger<StagingImporter> logger)
{
    private static readonly string[] CategoryFiles =
        ["watching.json", "later.json", "cancelled.json", "finished.json"];

    /// <summary>Parse every input and report the counts, writing nothing. Cross-checks against batch 57.</summary>
    public async Task<DryRunReport> DryRunAsync(CancellationToken ct)
    {
        var categoryCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var shows = new List<ParsedShow>();
        foreach (var file in CategoryFiles)
        {
            var parsed = MyShowsEnvelopeParser.ParseCategory(await ReadFileAsync(file, ct), file);
            categoryCounts[CategoryStatus(file)] = parsed.Count;
            shows.AddRange(parsed);
        }

        var episodes = MyShowsEnvelopeParser.ParseEpisodesFile(await ReadFileAsync("episodes.json", ct));
        var distinctEpisodeShows = episodes.Episodes.Select(e => e.ShowMyshowsId).Distinct().Count();

        var report = new DryRunReport(
            categoryCounts,
            shows.Count,
            episodes.Batch57Totals,
            distinctEpisodeShows,
            episodes.Episodes.Count);

        LogReport(report);
        return report;
    }

    /// <summary>Fill <c>staging_show</c> and <c>staging_episode</c>, inserting only rows not already present.</summary>
    public async Task<StageReport> StageAsync(CancellationToken ct)
    {
        var shows = new List<ParsedShow>();
        foreach (var file in CategoryFiles)
        {
            shows.AddRange(MyShowsEnvelopeParser.ParseCategory(await ReadFileAsync(file, ct), file));
        }

        var episodes = MyShowsEnvelopeParser.ParseEpisodesFile(await ReadFileAsync("episodes.json", ct));

        var existingShowIds = await db.StagingShows.Select(s => s.MyshowsId).ToListAsync(ct);
        var existingShowSet = existingShowIds.ToHashSet();
        var existingEpisodeKeys = (await db.StagingEpisodes
            .Select(e => new { e.ShowMyshowsId, e.SeasonNumber, e.EpisodeNumber }).ToListAsync(ct))
            .Select(k => (k.ShowMyshowsId, k.SeasonNumber, k.EpisodeNumber))
            .ToHashSet();

        var showsInserted = 0;
        foreach (var s in shows)
        {
            if (existingShowSet.Add(s.MyshowsId))
            {
                db.StagingShows.Add(new StagingShow
                {
                    MyshowsId = s.MyshowsId,
                    TitleRu = s.TitleRu,
                    TitleOriginal = s.TitleOriginal,
                    Year = s.Year,
                    Alias = s.Alias,
                    SourceFile = s.SourceFile,
                    SourceWatchStatus = s.SourceWatchStatus,
                    TotalEpisodes = s.TotalEpisodes,
                    WatchedEpisodes = s.WatchedEpisodes,
                    TotalSeasons = s.TotalSeasons,
                    // Resolution is filled by steps 2.2/2.3; nothing is known at stage time.
                    TvmazeResolution = "unresolved",
                    NeedsReview = false,
                });
                showsInserted++;
            }
        }

        var episodesInserted = 0;
        foreach (var e in episodes.Episodes)
        {
            if (existingEpisodeKeys.Add((e.ShowMyshowsId, e.SeasonNumber, e.EpisodeNumber)))
            {
                db.StagingEpisodes.Add(new StagingEpisode
                {
                    ShowMyshowsId = e.ShowMyshowsId,
                    SourceBatchId = e.SourceBatchId,
                    EpisodeIdSrc = e.EpisodeIdSrc,
                    SeasonNumber = e.SeasonNumber,
                    EpisodeNumber = e.EpisodeNumber,
                    Title = e.Title,
                    AirDateUtc = e.AirDateUtc,
                    IsSpecial = e.IsSpecial,
                    RuntimeMinutes = e.RuntimeMinutes,
                });
                episodesInserted++;
            }
        }

        // Persist the new rows first: the mirror rebuild below is a set-based UPDATE against the
        // database, so the freshly-inserted staging_show rows must exist before it can reach them.
        await db.SaveChangesAsync(ct);

        // Rebuild the denormalized imdb_id mirror from staging_imdb_id, the authoritative store the
        // scraper owns. staging_show.imdb_id is documented as "from staging_imdb_id" (SCHEMA.md §5), so
        // it is derived, not original. This matters after a --reset --stage cycle: --reset clears
        // staging_show but deliberately keeps staging_imdb_id (the scraped ids are expensive — myshows
        // sits behind DDoS protection and re-requesting them risks a ban), and the freshly-inserted rows
        // come back with imdb_id NULL. Without this the scraper would skip them (already resolved in
        // staging_imdb_id, so it never re-requests — good) yet never re-mirrors, leaving every id
        // stranded: v_staging_issues would report all of them as unresolved_imdb_id and the TVmaze lookup
        // (step 2.3) would see no id. Mirroring here keeps the precious ids wired in with zero requests.
        await RemirrorImdbIdsAsync(ct);

        var report = new StageReport(
            showsInserted, shows.Count - showsInserted, episodesInserted, episodes.Episodes.Count - episodesInserted);
        logger.LogInformation(
            "Staged {ShowsInserted} shows ({ShowsSkipped} already present), {EpsInserted} episodes "
            + "({EpsSkipped} already present).",
            report.ShowsInserted, report.ShowsSkipped, report.EpisodesInserted, report.EpisodesSkipped);
        return report;
    }

    /// <summary>
    /// Clear the staging tables this job owns. <c>staging_imdb_id</c> is left alone — the scraper owns
    /// it, and wiping it here would throw away scraped ids the user did not ask to lose.
    /// </summary>
    public async Task ResetAsync(CancellationToken ct)
    {
        await db.StagingEpisodes.ExecuteDeleteAsync(ct);
        await db.StagingShows.ExecuteDeleteAsync(ct);
        logger.LogInformation("Cleared staging_show and staging_episode (staging_imdb_id left intact).");
    }

    private async Task<string> ReadFileAsync(string fileName, CancellationToken ct)
    {
        var fullPath = Path.Combine(paths.InputDirectory, fileName);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException(
                $"Import input '{fileName}' not found under '{paths.InputDirectory}'.", fullPath);
        }

        return await File.ReadAllTextAsync(fullPath, ct);
    }

    // Set-based mirror of the authoritative staging_imdb_id.imdb_id onto the derived
    // staging_show.imdb_id (SCHEMA.md §5). UPDATE ... FROM needs SQLite 3.33+, which the bundled
    // provider exceeds. Only resolved ids propagate; a show with no resolved source row keeps whatever
    // it has (NULL for a fresh stage), so this never clears a value and never invents one.
    private async Task RemirrorImdbIdsAsync(CancellationToken ct) =>
        await db.Database.ExecuteSqlRawAsync(
            """
            UPDATE staging_show
               SET imdb_id = src.imdb_id
              FROM staging_imdb_id AS src
             WHERE staging_show.myshows_id = src.myshows_id
               AND src.imdb_id IS NOT NULL
               AND staging_show.imdb_id IS NULL;
            """,
            ct);

    private void LogReport(DryRunReport report)
    {
        logger.LogInformation(
            "Dry run: watching={Watching} later={Later} cancelled={Cancelled} finished={Finished} total={Total}.",
            Count(report, "watching"), Count(report, "later"), Count(report, "cancelled"),
            Count(report, "finished"), report.TotalShows);
        logger.LogInformation(
            "Batch 57 cross-check: {Batch57} (episode shows={EpisodeShows}, episode rows={EpisodeRows}).",
            string.Join(", ", report.Batch57Totals.Select(kv => $"{kv.Key}={kv.Value}")),
            report.DistinctEpisodeShows,
            report.EpisodeRowCount);

        foreach (var (status, count) in report.CategoryCounts)
        {
            if (report.Batch57Totals.TryGetValue(status, out var expected) && expected != count)
            {
                logger.LogWarning(
                    "Cross-check MISMATCH for '{Status}': category file has {Count}, batch 57 says {Expected}.",
                    status, count, expected);
            }
        }
    }

    private static int Count(DryRunReport report, string status) =>
        report.CategoryCounts.TryGetValue(status, out var c) ? c : 0;

    private static string CategoryStatus(string file) => Path.GetFileNameWithoutExtension(file);
}

/// <summary>What a dry run observed — the numbers step 2.1 verifies, plus the batch-57 cross-check.</summary>
internal sealed record DryRunReport(
    IReadOnlyDictionary<string, int> CategoryCounts,
    int TotalShows,
    IReadOnlyDictionary<string, int> Batch57Totals,
    int DistinctEpisodeShows,
    int EpisodeRowCount);

/// <summary>What a <c>--stage</c> wrote: inserted vs already-present, for shows and episodes.</summary>
internal sealed record StageReport(int ShowsInserted, int ShowsSkipped, int EpisodesInserted, int EpisodesSkipped);
