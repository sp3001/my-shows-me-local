using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Shows.Application;
using Shows.Domain;
using Shows.Infrastructure.Configuration;
using Shows.Infrastructure.Jobs;
using Shows.Infrastructure.Persistence;
using Shows.Infrastructure.Persistence.Repositories;
using Shows.Infrastructure.Persistence.Sources;
using Shows.Infrastructure.Tvmaze;
using Xunit;

namespace Shows.Tests.Integration;

/// <summary>
/// Acceptance for the <see cref="RefreshUpdatedShowJob"/> body, driven by a fake
/// <see cref="ICatalogProvider"/> (the bulk <c>/updates/shows</c> map) and a fake
/// <see cref="ITvmazeShowSource"/> (the per-show re-fetch) over a real temp DB — no network. The
/// assertions are the ones that fail silently when wrong: a show refreshed when its stored timestamp was
/// already current (idempotence broken), a soft-deleted show refreshed (R8 forgotten — burns API calls),
/// or an unreachable provider reported as a clean run (R14).
/// </summary>
public sealed class RefreshUpdatedJobTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SequentialIdGenerator _ids = new();

    public RefreshUpdatedJobTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"shows-refresh-updated-{Guid.NewGuid():N}.db");
        using var context = CreateContext();
        context.Database.EnsureCreated();
    }

    [Fact]
    public async Task Refreshes_only_shows_whose_provider_timestamp_is_newer()
    {
        // 101 stored at 1000, map says 2000 → refresh. 102 stored at 1000, map says 1000 → skip (not newer).
        // 103 stored at 1000, absent from the map → skip (no signal).
        SeedTracked(101, storedProviderUpdated: 1000);
        SeedTracked(102, storedProviderUpdated: 1000);
        SeedTracked(103, storedProviderUpdated: 1000);

        var provider = new FakeProvider().WithUpdates(new() { [101] = 2000, [102] = 1000 });
        var source = new FakeSource().With(101, "Running", episodes: 2, updated: 2000);

        var report = await RunJob(provider, source);

        Assert.Equal([101], source.RequestedIds.ToArray()); // only the genuinely-updated show is re-fetched
        Assert.Equal(1, report.Total);
        Assert.Equal(1, report.Changed);
    }

    [Fact]
    public async Task Second_run_refreshes_nothing_once_the_stored_timestamp_caught_up()
    {
        SeedTracked(201, storedProviderUpdated: 1000);
        var provider = new FakeProvider().WithUpdates(new() { [201] = 2000 });
        var source = new FakeSource().With(201, "Running", episodes: 2, updated: 2000);

        var first = await RunJob(provider, source);
        Assert.Equal(1, first.Changed);

        // The mapper stamped the source row's provider_updated to 2000, so the second run sees map == stored
        // and skips it. This is what makes items_changed honest on a re-run.
        var second = await RunJob(provider, source);
        Assert.Equal(0, second.Total);
        Assert.Equal(0, second.Changed);
    }

    [Fact]
    public async Task A_show_never_yet_stamped_is_refreshed_when_the_map_has_it()
    {
        // provider_updated null (a shell row) + a map entry → refresh. A missing stored timestamp is not a
        // reason to skip; the map is the only signal we have.
        SeedTracked(301, storedProviderUpdated: null);
        var provider = new FakeProvider().WithUpdates(new() { [301] = 5000 });
        var source = new FakeSource().With(301, "Running", episodes: 1, updated: 5000);

        var report = await RunJob(provider, source);

        Assert.Equal([301], source.RequestedIds.ToArray());
        Assert.Equal(1, report.Changed);
    }

    [Fact]
    public async Task Soft_deleted_show_is_never_refreshed()
    {
        // R8: a soft-deleted show keeps its tracked_show row. The bulk map may well contain it, but the
        // selection (deleted_at IS NULL) must exclude it so no re-fetch is spent on a show nobody tracks.
        SeedTracked(401, storedProviderUpdated: 1000, deleted: true);
        var provider = new FakeProvider().WithUpdates(new() { [401] = 9000 });
        var source = new FakeSource().With(401, "Running", episodes: 1, updated: 9000);

        await RunJob(provider, source);

        Assert.Empty(source.RequestedIds);
    }

    [Fact]
    public async Task Unreachable_provider_aborts_without_refreshing_anything()
    {
        // R14: the bulk map is the one whole-run network op. If TVmaze is unreachable we cannot know what
        // changed, so the job aborts (ProviderUnavailable) and fetches no per-show payload — never a clean
        // run that refreshed nothing.
        SeedTracked(501, storedProviderUpdated: 1000);
        var provider = new FakeProvider().Unavailable();
        var source = new FakeSource().With(501, "Running", episodes: 1, updated: 2000);

        var report = await RunJob(provider, source);

        Assert.True(report.ProviderUnavailable);
        Assert.False(report.Refused);
        Assert.Equal(0, report.Total);
        Assert.Empty(source.RequestedIds);
    }

    [Fact]
    public async Task Per_show_fetch_failure_is_a_failure_not_a_crash()
    {
        SeedTracked(601, storedProviderUpdated: 1000);
        SeedTracked(602, storedProviderUpdated: 1000);
        var provider = new FakeProvider().WithUpdates(new() { [601] = 2000, [602] = 2000 });
        var source = new FakeSource()
            .With(601, "Running", episodes: 1, updated: 2000)
            .Unavailable(602);

        var report = await RunJob(provider, source);

        Assert.Equal(2, report.Total);
        Assert.Equal(1, report.Changed); // 601 mapped
        Assert.Equal(1, report.Failed);  // 602 left for the next run
    }

    [Fact]
    public async Task Refuses_when_another_writer_holds_the_lease()
    {
        SeedTracked(701, storedProviderUpdated: 1000);
        var provider = new FakeProvider().WithUpdates(new() { [701] = 2000 });
        var source = new FakeSource().With(701, "Running", episodes: 1, updated: 2000);

        var report = await RunJob(provider, source, grantLease: false);

        Assert.True(report.Refused);
        Assert.Equal(0, provider.MapCalls); // refused before the bulk fetch
    }

    [Fact]
    public async Task Writes_a_job_run_row_with_total_and_changed()
    {
        SeedTracked(801, storedProviderUpdated: 1000);
        SeedTracked(802, storedProviderUpdated: 1000);
        var provider = new FakeProvider().WithUpdates(new() { [801] = 2000, [802] = 2000 });
        var source = new FakeSource()
            .With(801, "Running", episodes: 1, updated: 2000)
            .With(802, "Running", episodes: 1, updated: 2000);

        await RunJob(provider, source);

        await using var db = CreateContext();
        var run = db.JobRuns.Single(r => r.JobName == "RefreshUpdated");
        Assert.True(run.Succeeded);
        Assert.NotNull(run.FinishedAt);
        Assert.Equal(2, run.ItemsTotal);
        Assert.Equal(2, run.ItemsChanged);
    }

    // ---- helpers ----

    private async Task<RefreshUpdatedReport> RunJob(
        FakeProvider provider, FakeSource source, bool grantLease = true)
    {
        await using var db = CreateContext();
        var mapper = new TvmazeShowMapper(
            db, _ids, NullLogger<TvmazeShowMapper>.Instance, source);
        var job = new RefreshUpdatedShowJob(
            db,
            new TrackedShowRepositoryProxy(db),
            provider,
            mapper,
            new FakeLease(grantLease),
            _ids,
            new ShowsOptions(),
            NullLogger<RefreshUpdatedShowJob>.Instance);
        return await job.RunAsync(CancellationToken.None);
    }

    private void SeedTracked(int tvmazeId, long? storedProviderUpdated, bool deleted = false)
    {
        using var db = CreateContext();

        // The NOT NULL tvmaze_show_id FK needs a source row to point at (SCHEMA §2). The stored
        // provider_updated on that row is what the job compares the bulk map against.
        db.TvmazeShows.Add(new TvmazeShow
        {
            TvmazeId = tvmazeId,
            StatusRaw = string.Empty,
            RawJson = "{}",
            ProviderUpdated = storedProviderUpdated,
            FetchedAt = DateTimeOffset.UtcNow,
        });

        var show = new TrackedShow
        {
            Id = 10_000_000 + tvmazeId,
            TvmazeId = tvmazeId,
            Status = ShowStatus.Running,
            Title = $"Show {tvmazeId}",
        };
        db.Entry(show).Property("tvmaze_show_id").CurrentValue = tvmazeId;
        show.User = new UserShow
        {
            TrackedShowId = show.Id,
            Category = ShowCategory.Started,
            AddedAt = DateTimeOffset.UtcNow,
            CategoryChangedAt = DateTimeOffset.UtcNow,
            DeletedAt = deleted ? DateTimeOffset.UtcNow : null,
        };
        db.TrackedShows.Add(show);
        db.SaveChanges();
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

    /// <summary>The repository the job depends on, wired to the same context the job writes through.</summary>
    private sealed class TrackedShowRepositoryProxy(ShowsDbContext db) : ITrackedShowRepository
    {
        private readonly TrackedShowRepository _inner = new(db);

        public Task<IReadOnlyList<TrackedShow>> ListActiveByIdsAsync(IReadOnlyList<long> ids, CancellationToken ct) =>
            _inner.ListActiveByIdsAsync(ids, ct);

        public Task<IReadOnlyList<TrackedShow>> ListActiveAsync(CancellationToken ct) =>
            _inner.ListActiveAsync(ct);

        // Unused by this job; delegate to the real repository so the type stays honest.
        public Task<TrackedShow?> FindByIdAsync(long id, CancellationToken ct) => _inner.FindByIdAsync(id, ct);
        public Task<TrackedShow?> FindByIdWithGenresAsync(long id, CancellationToken ct) => _inner.FindByIdWithGenresAsync(id, ct);
        public Task<TrackedShow?> FindByTvmazeIdAsync(int id, CancellationToken ct) => _inner.FindByTvmazeIdAsync(id, ct);
        public Task<TrackedShow?> FindByTvmazeIdIncludingDeletedAsync(int id, CancellationToken ct) => _inner.FindByTvmazeIdIncludingDeletedAsync(id, ct);
        public Task<IReadOnlyList<TrackedShow>> ListActiveByCategoryAsync(ShowCategory c, CancellationToken ct) => _inner.ListActiveByCategoryAsync(c, ct);
        public Task<IReadOnlyList<TrackedShow>> ListActiveByStatusAsync(IReadOnlyCollection<ShowStatus> s, CancellationToken ct) => _inner.ListActiveByStatusAsync(s, ct);
        public Task<IReadOnlyList<TrackedShow>> ListActiveExcludingStatusAsync(IReadOnlyCollection<ShowStatus> s, CancellationToken ct) => _inner.ListActiveExcludingStatusAsync(s, ct);
        public Task<IReadOnlyList<TrackedShow>> ListIncludingUntrackedAsync(CancellationToken ct) => _inner.ListIncludingUntrackedAsync(ct);
        public Task<Dictionary<int, long?>> GetActiveTrackingByTvmazeIdsAsync(IReadOnlyList<int> ids, CancellationToken ct) => _inner.GetActiveTrackingByTvmazeIdsAsync(ids, ct);
        public Task<IReadOnlyList<Season>> ListSeasonsByShowIdAsync(long showId, CancellationToken ct) => _inner.ListSeasonsByShowIdAsync(showId, ct);
        public Task<IReadOnlyList<Season>> ListSeasonsByShowIdsAsync(IReadOnlyList<long> showIds, CancellationToken ct) => _inner.ListSeasonsByShowIdsAsync(showIds, ct);
        public Task AddAsync(TrackedShow show, CancellationToken ct) => _inner.AddAsync(show, ct);
    }

    /// <summary>Supplies the bulk <c>/updates/shows</c> map; can simulate the endpoint being unreachable.</summary>
    private sealed class FakeProvider : ICatalogProvider
    {
        private Dictionary<int, long> _updates = new();
        private bool _unavailable;

        /// <summary>How many times the bulk map was fetched (asserts a refused run never fetches).</summary>
        public int MapCalls { get; private set; }

        public FakeProvider WithUpdates(Dictionary<int, long> updates)
        {
            _updates = updates;
            return this;
        }

        public FakeProvider Unavailable()
        {
            _unavailable = true;
            return this;
        }

        public Task<IReadOnlyDictionary<int, long>> GetShowUpdateTimestampsAsync(CancellationToken cancellationToken)
        {
            MapCalls++;
            if (_unavailable)
            {
                throw new TvmazeUnavailableException("api.tvmaze.com unreachable", new IOException("reset"));
            }

            return Task.FromResult<IReadOnlyDictionary<int, long>>(_updates);
        }

        // Unused by this job.
        public Task<IReadOnlyList<CatalogShow>> SearchAsync(string q, CancellationToken ct) => throw new NotSupportedException();
        public Task<CatalogShow?> GetShowAsync(int id, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<EpisodeCatalogInfo>> GetEpisodesAsync(int id, CancellationToken ct) => throw new NotSupportedException();
        public Task<int?> LookupByImdbIdAsync(string imdb, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<ShowAka>> GetAkasAsync(int id, CancellationToken ct) => throw new NotSupportedException();
    }

    /// <summary>Supplies the per-show re-fetch payload; can simulate one show being unreachable.</summary>
    private sealed class FakeSource : ITvmazeShowSource
    {
        private readonly Dictionary<int, TvmazePayload?> _payloads = new();
        private readonly HashSet<int> _unavailable = [];
        public List<int> RequestedIds { get; } = [];

        public FakeSource With(int tvmazeId, string status, int episodes, long updated)
        {
            var show = new TvmazeShow
            {
                TvmazeId = tvmazeId,
                Name = $"Show {tvmazeId}",
                TypeRaw = "Scripted",
                StatusRaw = status,
                Premiered = "2015-01-01",
                GenresRaw = """["Drama"]""",
                ProviderUpdated = updated,
                RawJson = "{}",
                FetchedAt = DateTimeOffset.UtcNow,
            };

            var eps = new List<TvmazeEpisode>();
            for (var i = 1; i <= episodes; i++)
            {
                eps.Add(new TvmazeEpisode
                {
                    TvmazeEpisodeId = tvmazeId * 1000 + i,
                    TvmazeShowId = tvmazeId,
                    Name = $"E{i}",
                    SeasonNumber = 1,
                    Number = i,
                    TypeRaw = "regular",
                    Airdate = $"2015-01-{i:D2}",
                    RawJson = "{}",
                    FetchedAt = DateTimeOffset.UtcNow,
                });
            }

            _payloads[tvmazeId] = new TvmazePayload(show, eps);
            return this;
        }

        public FakeSource Unavailable(int tvmazeId)
        {
            _unavailable.Add(tvmazeId);
            return this;
        }

        public Task<TvmazePayload?> FetchAsync(int tvmazeId, CancellationToken cancellationToken)
        {
            RequestedIds.Add(tvmazeId);
            if (_unavailable.Contains(tvmazeId))
            {
                throw new TvmazeUnavailableException("tvmaze.example unreachable", new IOException("reset"));
            }

            return Task.FromResult(_payloads.TryGetValue(tvmazeId, out var p) ? p : null);
        }
    }

    private sealed class FakeLease(bool grant) : IWriterLease
    {
        public Task<IWriterLeaseHandle?> TryAcquireAsync(string name, CancellationToken cancellationToken) =>
            Task.FromResult<IWriterLeaseHandle?>(grant ? new Handle() : null);

        private sealed class Handle : IWriterLeaseHandle
        {
            public string Name => "refresh_updated";
            public Task<bool> RenewAsync(CancellationToken cancellationToken) => Task.FromResult(true);
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class SequentialIdGenerator : IIdGenerator
    {
        private long _next = 1_000_000;
        public long NextId() => _next++;
    }
}
