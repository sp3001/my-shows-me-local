using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Shows.Application;
using Shows.Infrastructure.Configuration;
using Shows.Infrastructure.Import;
using Shows.Infrastructure.Persistence;
using Shows.Infrastructure.Persistence.Staging;
using Shows.Infrastructure.Tvmaze;
using Xunit;

namespace Shows.Tests.Integration;

/// <summary>
/// Step 2.3 acceptance for the resolver's control logic, driven by a fake <see cref="ICatalogProvider"/>
/// so no network is touched: it records a hit as <c>scraped</c> with the id, a 404 as <c>unresolved</c>,
/// a transport failure as neither (left for the next run, counted failed — R14), is resumable (skips a
/// show that already has a <c>tvmaze_id</c>, including a manual one), honours the per-run cap, and refuses
/// when another writer holds the lease.
/// </summary>
public sealed class TvmazeIdResolverTests : IDisposable
{
    private readonly string _dbPath;

    public TvmazeIdResolverTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"shows-2-3-{Guid.NewGuid():N}.db");
        using var context = CreateContext();
        context.Database.Migrate();
    }

    [Fact]
    public async Task Records_hit_as_scraped_and_404_as_unresolved()
    {
        SeedShow(1, imdb: "tt1111111");
        SeedShow(2, imdb: "tt2222222");
        var provider = new FakeProvider().Hit("tt1111111", 305).Miss("tt2222222");

        var report = await RunResolver(provider);

        Assert.False(report.Refused);
        Assert.Equal(2, report.Attempted);
        Assert.Equal(1, report.Resolved);
        Assert.Equal(1, report.Unresolved);
        Assert.Equal(0, report.Failed);

        using var db = CreateContext();
        var hit = db.StagingShows.Single(s => s.MyshowsId == 1);
        Assert.Equal(305, hit.TvmazeId);
        Assert.Equal("scraped", hit.TvmazeResolution);

        var miss = db.StagingShows.Single(s => s.MyshowsId == 2);
        Assert.Null(miss.TvmazeId);
        Assert.Equal("unresolved", miss.TvmazeResolution);
    }

    [Fact]
    public async Task A_transport_failure_is_left_for_the_next_run_not_marked_unresolved()
    {
        // R14: a dead proxy must not look like "TVmaze has no data". The row is not written and the run
        // counts it as failed, not unresolved.
        SeedShow(1, imdb: "tt1111111");
        var provider = new FakeProvider().ThrowOn("tt1111111");

        var report = await RunResolver(provider);

        Assert.Equal(1, report.Attempted);
        Assert.Equal(0, report.Resolved);
        Assert.Equal(0, report.Unresolved);   // NOT counted as a genuine 404
        Assert.Equal(1, report.Failed);

        using var db = CreateContext();
        var row = db.StagingShows.Single(s => s.MyshowsId == 1);
        Assert.Null(row.TvmazeId);
        Assert.Equal("unresolved", row.TvmazeResolution); // unchanged from seed — never written
    }

    [Fact]
    public async Task Is_resumable_and_never_touches_a_show_that_already_has_a_tvmaze_id()
    {
        // Show 1 already scraped; show 2 carries the user's MANUAL id (96144's analogue). Neither is a
        // target, so neither is looked up and the manual resolution is preserved.
        SeedShow(1, imdb: "tt1111111", tvmazeId: 100, resolution: "scraped");
        SeedShow(2, imdb: "tt2222222", tvmazeId: 86754, resolution: "manual");
        SeedShow(3, imdb: "tt3333333"); // the only real target
        var provider = new FakeProvider().Hit("tt3333333", 777);

        var report = await RunResolver(provider);

        Assert.Equal(1, report.Attempted);
        Assert.Equal(["tt3333333"], provider.RequestedImdbIds); // only the un-resolved target
        Assert.Equal(2, report.AlreadyResolved); // shows 1 (scraped) and 2 (manual) both had a tvmaze_id

        using var db = CreateContext();
        Assert.Equal(100, db.StagingShows.Single(s => s.MyshowsId == 1).TvmazeId);
        var manual = db.StagingShows.Single(s => s.MyshowsId == 2);
        Assert.Equal(86754, manual.TvmazeId);
        Assert.Equal("manual", manual.TvmazeResolution); // never overwritten
        Assert.Equal(777, db.StagingShows.Single(s => s.MyshowsId == 3).TvmazeId);
    }

    [Fact]
    public async Task Honours_the_per_run_cap_lowest_id_first()
    {
        SeedShow(1, imdb: "tt1111111");
        SeedShow(2, imdb: "tt2222222");
        SeedShow(3, imdb: "tt3333333");
        var provider = new FakeProvider().Hit("tt1111111", 1).Hit("tt2222222", 2).Hit("tt3333333", 3);

        var report = await RunResolver(provider, maxRequestsPerRun: 2);

        Assert.Equal(2, report.Attempted);
        Assert.Equal(["tt1111111", "tt2222222"], provider.RequestedImdbIds); // lowest myshows_id first
    }

    [Fact]
    public async Task Refuses_when_another_writer_holds_the_lease()
    {
        SeedShow(1, imdb: "tt1111111");
        var provider = new FakeProvider().Hit("tt1111111", 1);

        var report = await RunResolver(provider, grantLease: false);

        Assert.True(report.Refused);
        Assert.Equal(0, report.Attempted);
        Assert.Empty(provider.RequestedImdbIds);
    }

    // ---- helpers ----

    private void SeedShow(int id, string? imdb, int? tvmazeId = null, string resolution = "unresolved")
    {
        using var db = CreateContext();
        db.StagingShows.Add(new StagingShow
        {
            MyshowsId = id,
            ImdbId = imdb,
            TvmazeId = tvmazeId,
            TvmazeResolution = resolution,
            SourceFile = "watching.json",
            SourceWatchStatus = "watching",
            TotalEpisodes = 1,
            WatchedEpisodes = 0,
        });
        db.SaveChanges();
    }

    private async Task<ResolveReport> RunResolver(
        FakeProvider provider, int maxRequestsPerRun = 0, bool grantLease = true)
    {
        using var db = CreateContext();
        var options = new ShowsOptions();
        options.Tvmaze.MaxRequestsPerRun = maxRequestsPerRun;
        var resolver = new TvmazeIdResolver(
            db, provider, new FakeLease(grantLease), options, NullLogger<TvmazeIdResolver>.Instance);
        return await resolver.RunAsync(CancellationToken.None);
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

    private sealed class FakeProvider : ICatalogProvider
    {
        private readonly Dictionary<string, int?> _byImdb = new();
        private readonly HashSet<string> _throw = new();

        public List<string> RequestedImdbIds { get; } = [];

        public FakeProvider Hit(string imdb, int tvmazeId)
        {
            _byImdb[imdb] = tvmazeId;
            return this;
        }

        public FakeProvider Miss(string imdb)
        {
            _byImdb[imdb] = null;
            return this;
        }

        public FakeProvider ThrowOn(string imdb)
        {
            _throw.Add(imdb);
            return this;
        }

        public Task<int?> LookupByImdbIdAsync(string imdbId, CancellationToken cancellationToken)
        {
            RequestedImdbIds.Add(imdbId);
            if (_throw.Contains(imdbId))
            {
                throw new TvmazeUnavailableException("api.tvmaze.com unreachable (fake)");
            }

            return Task.FromResult(_byImdb.TryGetValue(imdbId, out var v) ? v : null);
        }

        public Task<IReadOnlyList<CatalogShow>> SearchAsync(string query, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<CatalogShow?> GetShowAsync(int tvmazeId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<EpisodeCatalogInfo>> GetEpisodesAsync(int tvmazeId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<CatalogShow>> GetNewShowsAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<CatalogShow>> GetUpcomingAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<ShowAka>> GetAkasAsync(int tvmazeId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyDictionary<int, long>> GetShowUpdateTimestampsAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class FakeLease(bool grant) : IWriterLease
    {
        public Task<IWriterLeaseHandle?> TryAcquireAsync(string name, CancellationToken cancellationToken) =>
            Task.FromResult<IWriterLeaseHandle?>(grant ? new Handle() : null);

        private sealed class Handle : IWriterLeaseHandle
        {
            public string Name => "resolve_tvmaze";

            public Task<bool> RenewAsync(CancellationToken cancellationToken) => Task.FromResult(true);

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
