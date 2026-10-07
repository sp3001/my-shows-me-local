using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Shows.Application;
using Shows.Infrastructure.Configuration;
using Shows.Infrastructure.Import;
using Shows.Infrastructure.Persistence;
using Shows.Infrastructure.Persistence.Staging;
using Xunit;

namespace Shows.Tests.Integration;

/// <summary>
/// Step 2.2 acceptance for the scraper's control logic, driven by a fake fetcher so no network is
/// touched: it classifies resolved / no-id / failed, records transport failures as data (not throws),
/// mirrors the id onto <c>staging_show.imdb_id</c>, is resumable (skips already-resolved, retries
/// errored), honours the per-run cap, and refuses when another writer holds the lease.
/// </summary>
public sealed class ImdbIdScraperTests : IDisposable
{
    private readonly string _dbPath;

    public ImdbIdScraperTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"shows-2-2-{Guid.NewGuid():N}.db");
        using var context = CreateContext();
        context.Database.Migrate();
    }

    [Fact]
    public async Task Classifies_resolved_no_id_and_failed_and_mirrors_the_imdb_id()
    {
        SeedShows(1, 2, 3);
        var fetcher = new FakeFetcher()
            .Ok(1, """
                <a href="https://www.imdb.com/title/tt2085059/">x</a>
                <a href="https://www.kinopoisk.ru/film/655800/">k</a>
                """)
            .Ok(2, "<html>no imdb link here</html>")
            .TransportFail(3);

        var report = await RunScraper(fetcher);

        Assert.False(report.Refused);
        Assert.Equal(3, report.Attempted);
        Assert.Equal(1, report.Resolved);
        Assert.Equal(1, report.NoId);
        Assert.Equal(1, report.Failed);

        using var db = CreateContext();
        var r1 = db.StagingImdbIds.Single(i => i.MyshowsId == 1);
        Assert.Equal("tt2085059", r1.ImdbId);
        Assert.Equal("655800", r1.KinopoiskId);
        Assert.Equal(200, r1.HttpStatus);
        Assert.Null(r1.Error);

        var r2 = db.StagingImdbIds.Single(i => i.MyshowsId == 2);
        Assert.Null(r2.ImdbId);
        Assert.Equal("no imdb id on page", r2.Error);
        Assert.Equal(200, r2.HttpStatus);

        var r3 = db.StagingImdbIds.Single(i => i.MyshowsId == 3);
        Assert.Null(r3.ImdbId);
        Assert.Null(r3.HttpStatus); // transport failure: no status reached us
        Assert.StartsWith("transport:", r3.Error);

        // Mirror only the resolved id onto the denormalized copy.
        Assert.Equal("tt2085059", db.StagingShows.Single(s => s.MyshowsId == 1).ImdbId);
        Assert.Null(db.StagingShows.Single(s => s.MyshowsId == 2).ImdbId);
    }

    [Fact]
    public async Task Resumable_skips_resolved_and_retries_errored()
    {
        SeedShows(1, 2, 3);
        // Pre-seed: show 1 already resolved, show 2 previously failed (error set, no id).
        using (var seed = CreateContext())
        {
            seed.StagingImdbIds.Add(new StagingImdbId { MyshowsId = 1, ImdbId = "tt1111111", ScrapedAt = DateTimeOffset.UtcNow });
            seed.StagingImdbIds.Add(new StagingImdbId { MyshowsId = 2, Error = "transport: boom" });
            await seed.SaveChangesAsync();
        }

        var fetcher = new FakeFetcher().Ok(2, """<a href="https://www.imdb.com/title/tt2222222/">x</a>""").Ok(3, """<a href="https://www.imdb.com/title/tt3333333/">x</a>""");
        var report = await RunScraper(fetcher);

        // Show 1 is skipped (already resolved); shows 2 (retry) and 3 are fetched.
        Assert.Equal(2, report.Attempted);
        Assert.Equal(1, report.AlreadyResolved);
        Assert.Equal(2, report.Resolved);
        Assert.Equal([2, 3], fetcher.RequestedIds);
    }

    [Fact]
    public async Task Cap_limits_requests_per_run()
    {
        SeedShows(1, 2, 3, 4, 5);
        var fetcher = new FakeFetcher();
        foreach (var id in new[] { 1, 2, 3 })
        {
            fetcher.Ok(id, """<a href="https://www.imdb.com/title/tt1000000/">x</a>""");
        }

        var report = await RunScraper(fetcher, maxRequestsPerRun: 3);

        Assert.Equal(3, report.Attempted);
        Assert.Equal([1, 2, 3], fetcher.RequestedIds); // capped, lowest ids first
    }

    [Fact]
    public async Task Refuses_when_lease_is_held_by_another_writer()
    {
        SeedShows(1);
        var report = await RunScraper(new FakeFetcher(), grantLease: false);

        Assert.True(report.Refused);
        Assert.Equal(0, report.Attempted);
    }

    [Fact]
    public async Task Ambiguous_page_is_recorded_as_failure_not_resolved()
    {
        SeedShows(1);
        var fetcher = new FakeFetcher().Ok(1, """
            <a href="https://www.imdb.com/title/tt1111111/">a</a>
            <a href="https://www.imdb.com/title/tt2222222/">b</a>
            """);

        var report = await RunScraper(fetcher);
        Assert.Equal(1, report.Failed);

        using var db = CreateContext();
        var row = db.StagingImdbIds.Single(i => i.MyshowsId == 1);
        Assert.Null(row.ImdbId);
        Assert.Equal("ambiguous imdb ids on page", row.Error);
    }

    private void SeedShows(params int[] ids)
    {
        using var db = CreateContext();
        foreach (var id in ids)
        {
            db.StagingShows.Add(new StagingShow
            {
                MyshowsId = id,
                SourceFile = "watching.json",
                SourceWatchStatus = "watching",
                TotalEpisodes = 1,
                WatchedEpisodes = 0,
                TvmazeResolution = "unresolved",
            });
        }

        db.SaveChanges();
    }

    private async Task<ScrapeReport> RunScraper(
        FakeFetcher fetcher, int maxRequestsPerRun = 0, bool grantLease = true)
    {
        using var db = CreateContext();
        var options = new ShowsOptions();
        options.MyShows.MaxRequestsPerRun = maxRequestsPerRun;
        var scraper = new ImdbIdScraper(
            db, fetcher, new FakeLease(grantLease), options, NullLogger<ImdbIdScraper>.Instance);
        return await scraper.RunAsync(CancellationToken.None);
    }

    private ShowsDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<ShowsDbContext>()
            .UseSqlite(new SqliteConnectionStringBuilder { DataSource = _dbPath }.ToString())
            .UseSnakeCaseNamingConvention()
            .AddInterceptors(new SqlitePragmaInterceptor())
            .Options;
        return new ShowsDbContext(options);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath))
        {
            File.Delete(_dbPath);
        }
    }

    private sealed class FakeFetcher : IMyShowsPageFetcher
    {
        private readonly Dictionary<int, PageFetchResult> _results = new();
        public List<int> RequestedIds { get; } = [];

        public FakeFetcher Ok(int id, string html)
        {
            _results[id] = new PageFetchResult(200, html, null);
            return this;
        }

        public FakeFetcher TransportFail(int id)
        {
            _results[id] = new PageFetchResult(null, null, "transport: HttpRequestException: reset");
            return this;
        }

        public Task<PageFetchResult> FetchAsync(int myshowsId, CancellationToken cancellationToken)
        {
            RequestedIds.Add(myshowsId);
            return Task.FromResult(_results.TryGetValue(myshowsId, out var r)
                ? r
                : new PageFetchResult(200, "<html></html>", null));
        }
    }

    private sealed class FakeLease(bool grant) : IWriterLease
    {
        public Task<IWriterLeaseHandle?> TryAcquireAsync(string name, CancellationToken cancellationToken) =>
            Task.FromResult<IWriterLeaseHandle?>(grant ? new Handle() : null);

        private sealed class Handle : IWriterLeaseHandle
        {
            public string Name => "scrape_myshows";

            public Task<bool> RenewAsync(CancellationToken cancellationToken) => Task.FromResult(true);

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
