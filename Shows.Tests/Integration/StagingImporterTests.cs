using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shows.Infrastructure.Configuration;
using Shows.Infrastructure.Import;
using Shows.Infrastructure.Persistence;
using Shows.Infrastructure.Persistence.Staging;
using Xunit;

namespace Shows.Tests.Integration;

/// <summary>
/// Step 2.1 acceptance for the staging writer: <c>--stage</c> fills <c>staging_show</c> /
/// <c>staging_episode</c> from the exports, is idempotent on <c>myshows_id</c> (it must never clobber a
/// user's DB-Browser edits between stage and promote), and <c>--reset</c> clears only the tables this
/// job owns. Runs against a migrated temp database and a temp input directory of synthetic exports.
/// </summary>
public sealed class StagingImporterTests : IDisposable
{
    private readonly string _inputDir;
    private readonly string _dbPath;

    public StagingImporterTests()
    {
        var root = Path.Combine(Path.GetTempPath(), $"shows-2-1-{Guid.NewGuid():N}");
        _inputDir = Path.Combine(root, "input");
        Directory.CreateDirectory(_inputDir);
        _dbPath = Path.Combine(root, "shows.db");

        // Two category files and one episodes.json, shaped exactly like the real exports.
        Write("watching.json", """
            { "result": [
              { "show": { "id": 100, "title": "Alpha\t", "titleOriginal": "Alpha", "year": 2020, "totalSeasons": 2 },
                "watchStatus": "watching", "totalEpisodes": 10, "watchedEpisodes": 10 },
              { "show": { "id": 101, "title": "Beta", "titleOriginal": "Beta", "year": 2021 },
                "watchStatus": "watching", "totalEpisodes": 0, "watchedEpisodes": 0 } ] }
            """);
        Write("later.json", """
            { "result": [
              { "show": { "id": 200, "title": "Gamma", "titleOriginal": "Gamma", "year": 2019 },
                "watchStatus": "later", "totalEpisodes": 5, "watchedEpisodes": 0 } ] }
            """);
        Write("cancelled.json", "{ \"result\": [] }");
        Write("finished.json", """
            { "result": [
              { "show": { "id": 300, "title": "Delta", "titleOriginal": "Delta", "year": 2018 },
                "watchStatus": "finished", "totalEpisodes": 8, "watchedEpisodes": 8 } ] }
            """);
        Write("episodes.json", """
            [
              { "id": 60, "result": [] },
              { "id": 57, "result": [
                  { "watchStatus": "watching", "count": 2, "shows": [] },
                  { "watchStatus": "later", "count": 1, "shows": [] },
                  { "watchStatus": "cancelled", "count": 0, "shows": [] },
                  { "watchStatus": "finished", "count": 1, "shows": [] } ] },
              { "id": 58, "result": [
                  { "started": true, "show": { "id": 100 },
                    "episodes": [
                      { "id": 9001, "seasonNumber": 1, "episodeNumber": 2, "title": "A2", "runtime": 22 },
                      { "id": 9002, "seasonNumber": 1, "episodeNumber": 3, "title": "A3", "isSpecial": true } ] },
                  { "started": false, "show": { "id": 200 },
                    "episodes": [
                      { "id": 9003, "seasonNumber": 1, "episodeNumber": 1, "title": "G1" } ] } ] }
            ]
            """);

        using var context = CreateContext();
        context.Database.Migrate();
    }

    [Fact]
    public async Task Dry_run_reports_category_counts_total_and_distinct_episode_shows()
    {
        using var importer = CreateImporter();
        var report = await importer.DryRunAsync(CancellationToken.None);

        Assert.Equal(2, report.CategoryCounts["watching"]);
        Assert.Equal(1, report.CategoryCounts["later"]);
        Assert.Equal(0, report.CategoryCounts["cancelled"]);
        Assert.Equal(1, report.CategoryCounts["finished"]);
        Assert.Equal(4, report.TotalShows);

        // Batch 57 cross-check agrees with the category files.
        Assert.Equal(2, report.Batch57Totals["watching"]);
        Assert.Equal(1, report.Batch57Totals["later"]);

        // Two distinct shows carry episode arrays (100 and 200); show 101/300 have none.
        Assert.Equal(2, report.DistinctEpisodeShows);
        Assert.Equal(3, report.EpisodeRowCount);
    }

    [Fact]
    public async Task Stage_fills_staging_tables_with_trimmed_titles_and_unresolved_resolution()
    {
        using (var importer = CreateImporter())
        {
            await importer.StageAsync(CancellationToken.None);
        }

        using var context = CreateContext();
        var alpha = context.StagingShows.Single(s => s.MyshowsId == 100);
        Assert.Equal("Alpha", alpha.TitleRu); // trailing tab trimmed
        Assert.Equal("watching", alpha.SourceWatchStatus);
        Assert.Equal("watching.json", alpha.SourceFile);
        Assert.Equal("unresolved", alpha.TvmazeResolution);
        Assert.False(alpha.NeedsReview);
        Assert.Null(alpha.PromotedAt);

        Assert.Equal(4, await context.StagingShows.CountAsync());
        Assert.Equal(3, await context.StagingEpisodes.CountAsync());
        Assert.Equal(2, context.StagingEpisodes.Select(e => e.ShowMyshowsId).Distinct().Count());
    }

    [Fact]
    public async Task Stage_is_idempotent_and_preserves_user_edits()
    {
        using (var importer = CreateImporter())
        {
            await importer.StageAsync(CancellationToken.None);
        }

        // Simulate the user editing a staging row in DB Browser between stage and promote.
        using (var edit = CreateContext())
        {
            var row = edit.StagingShows.Single(s => s.MyshowsId == 100);
            row.TitleRu = "USER EDIT";
            await edit.SaveChangesAsync();
        }

        StageReport second;
        using (var importer = CreateImporter())
        {
            second = await importer.StageAsync(CancellationToken.None);
        }
        Assert.Equal(0, second.ShowsInserted);
        Assert.Equal(0, second.EpisodesInserted);

        using var after = CreateContext();
        Assert.Equal(4, await after.StagingShows.CountAsync());
        Assert.Equal(3, await after.StagingEpisodes.CountAsync());
        // The re-run must not have clobbered the manual edit.
        Assert.Equal("USER EDIT", after.StagingShows.Single(s => s.MyshowsId == 100).TitleRu);
    }

    [Fact]
    public async Task Reset_clears_owned_tables_but_leaves_staging_imdb_id()
    {
        using (var importer = CreateImporter())
        {
            await importer.StageAsync(CancellationToken.None);
        }
        using (var seed = CreateContext())
        {
            seed.StagingImdbIds.Add(new StagingImdbId { MyshowsId = 100, ImdbId = "tt1" });
            await seed.SaveChangesAsync();
        }

        using (var importer = CreateImporter())
        {
            await importer.ResetAsync(CancellationToken.None);
        }

        using var after = CreateContext();
        Assert.Equal(0, await after.StagingShows.CountAsync());
        Assert.Equal(0, await after.StagingEpisodes.CountAsync());
        Assert.Equal(1, await after.StagingImdbIds.CountAsync()); // owned by the scraper, not this job
    }

    [Fact]
    public async Task Reset_then_stage_remirrors_saved_imdb_ids_without_losing_them()
    {
        // Cycle a real scrape through a reset/re-stage: the scraped id lives in staging_imdb_id (which
        // --reset keeps), and the derived staging_show.imdb_id mirror must be rebuilt by --stage so the
        // id is not stranded after the mirror row was wiped. No scraper runs here — the id must survive
        // purely from staging_imdb_id.
        using (var importer = CreateImporter())
        {
            await importer.StageAsync(CancellationToken.None);
        }
        using (var seed = CreateContext())
        {
            seed.StagingImdbIds.Add(new StagingImdbId { MyshowsId = 100, ImdbId = "tt2085059" });
            seed.StagingShows.Single(s => s.MyshowsId == 100).ImdbId = "tt2085059"; // as the scraper would
            await seed.SaveChangesAsync();
        }

        using (var importer = CreateImporter())
        {
            await importer.ResetAsync(CancellationToken.None);
            await importer.StageAsync(CancellationToken.None);
        }

        using var after = CreateContext();
        Assert.Equal(1, await after.StagingImdbIds.CountAsync()); // never deleted
        Assert.Equal("tt2085059", after.StagingImdbIds.Single(i => i.MyshowsId == 100).ImdbId);
        // The freshly re-staged row's mirror is rebuilt from the authoritative source.
        Assert.Equal("tt2085059", after.StagingShows.Single(s => s.MyshowsId == 100).ImdbId);
        // A show with no scraped id stays null (the mirror never invents a value).
        Assert.Null(after.StagingShows.Single(s => s.MyshowsId == 101).ImdbId);
    }

    private void Write(string name, string content) =>
        File.WriteAllText(Path.Combine(_inputDir, name), content);

    private ImporterHandle CreateImporter() => new(_dbPath, CreatePaths());

    private DataPaths CreatePaths()
    {
        var options = Options.Create(new ShowsOptions
        {
            Data =
            {
                BaseDirectory = Path.GetDirectoryName(_dbPath)!,
                DatabasePath = Path.GetFileName(_dbPath),
                InputDirectory = _inputDir,
            },
        });
        return new DataPaths(options);
    }

    private ShowsDbContext CreateContext() => NewContext(_dbPath);

    private static ShowsDbContext NewContext(string dbPath)
    {
        var options = new DbContextOptionsBuilder<ShowsDbContext>()
            .UseSqlite(new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString())
            .UseSnakeCaseNamingConvention()
            .AddInterceptors(new SqlitePragmaInterceptor())
            .Options;
        return new ShowsDbContext(options);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        var root = Path.GetDirectoryName(_inputDir)!;
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// Owns the <see cref="ShowsDbContext"/> the importer is built over so the test can dispose it
    /// deterministically (CA2000) while still calling the importer's three methods directly.
    /// </summary>
    private sealed class ImporterHandle : IDisposable
    {
        private readonly ShowsDbContext _context;
        private readonly StagingImporter _importer;

        public ImporterHandle(string dbPath, DataPaths paths)
        {
            _context = NewContext(dbPath);
            _importer = new StagingImporter(_context, paths, NullLogger<StagingImporter>.Instance);
        }

        public Task<DryRunReport> DryRunAsync(CancellationToken ct) => _importer.DryRunAsync(ct);

        public Task<StageReport> StageAsync(CancellationToken ct) => _importer.StageAsync(ct);

        public Task ResetAsync(CancellationToken ct) => _importer.ResetAsync(ct);

        public void Dispose() => _context.Dispose();
    }
}
