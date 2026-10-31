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
/// Steps 7.1 (EpisodeRefresh) + 7.2 (StatusRecheck) acceptance for the shared
/// <see cref="ShowRefreshJob"/> body, driven by a fake <see cref="ITvmazeShowSource"/> so no network is
/// touched. The assertions are the ones that fail silently when wrong: a second run that reports a change
/// (idempotency broken), a soft-deleted show that still gets fetched (R8 forgotten — burns API calls on
/// shows nobody tracks), or a refresh that regenerates an episode id and orphans a watch mark.
/// </summary>
public sealed class ShowRefreshJobTests : IDisposable
{
    private readonly string _dbPath;

    // One id source for the whole test: the job_run id and the mapper's episode/season ids must never
    // collide across the multiple RunJob calls a test makes (a fresh generator per run would re-mint 0).
    private readonly SequentialIdGenerator _ids = new();

    public ShowRefreshJobTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"shows-7-{Guid.NewGuid():N}.db");
        using var context = CreateContext();
        context.Database.EnsureCreated();
    }

    [Fact]
    public async Task Second_run_of_an_unchanged_catalog_reports_zero_changes()
    {
        SeedTracked(101, ShowStatus.Running);
        var source = new FakeSource();
        source.With(101, "Running", episodes: 3);

        var first = await RunJob(RefreshMode.EpisodeRefresh, source);
        Assert.False(first.Refused);
        Assert.Equal(1, first.Total);
        Assert.Equal(1, first.Changed); // first map fills the catalog

        // A second run over the SAME payload must change nothing — this is what makes items_changed
        // honest (SPEC step 7.x). If the mapper regenerated ids or re-stamped mapped_at, this would be 1.
        var second = await RunJob(RefreshMode.EpisodeRefresh, source);
        Assert.Equal(1, second.Total);
        Assert.Equal(0, second.Changed);
    }

    [Fact]
    public async Task EpisodeRefresh_selects_airing_and_skips_ended_or_canceled()
    {
        SeedTracked(201, ShowStatus.Running);
        SeedTracked(202, ShowStatus.Ended);
        SeedTracked(203, ShowStatus.Canceled);
        SeedTracked(204, ShowStatus.Unknown);

        var source = new FakeSource();
        foreach (var id in new[] { 201, 202, 203, 204 })
        {
            source.With(id, "Running", episodes: 1);
        }

        var report = await RunJob(RefreshMode.EpisodeRefresh, source);

        // EpisodeRefresh excludes Ended/Canceled (R2: status is the provider's, not the user's category).
        // Running and Unknown are refreshed so an airing catalog never silently rots.
        Assert.Equal([201, 204], source.RequestedIds.Order().ToArray());
        Assert.Equal(2, report.Total);
    }

    [Fact]
    public async Task StatusRecheck_selects_only_ended_or_canceled_and_logs_a_resume()
    {
        SeedTracked(301, ShowStatus.Ended);
        SeedTracked(302, ShowStatus.Running);

        var source = new FakeSource();
        // Show 301 "resumed": TVmaze now reports Running. The mapper flips the enum; the job notices the
        // transition without reading the source table (source-table containment, step 8.1).
        source.With(301, "Running", episodes: 1);
        source.With(302, "Running", episodes: 1);

        var report = await RunJob(RefreshMode.StatusRecheck, source);

        Assert.Equal([301], source.RequestedIds.ToArray()); // only the Ended show is rechecked
        Assert.Equal(1, report.Changed);

        await using var db = CreateContext();
        Assert.Equal(ShowStatus.Running, db.TrackedShows.Single(s => s.TvmazeId == 301).Status);
    }

    [Fact]
    public async Task Soft_deleted_show_is_never_fetched()
    {
        // R8: a soft-deleted show keeps its tracked_show row. A job that forgot deleted_at IS NULL would
        // spend an external API call on it, silently. The selection query is the only guard.
        SeedTracked(401, ShowStatus.Running, deleted: true);
        SeedTracked(402, ShowStatus.Running);

        var source = new FakeSource().With(401, "Running", 1).With(402, "Running", 1);
        await RunJob(RefreshMode.EpisodeRefresh, source);

        Assert.Equal([402], source.RequestedIds.ToArray());
    }

    [Fact]
    public async Task Refresh_leaves_every_watch_mark_intact()
    {
        SeedTracked(501, ShowStatus.Running);
        var source = new FakeSource().With(501, "Running", episodes: 3);
        await RunJob(RefreshMode.EpisodeRefresh, source);

        // The tracked_show id is 10_000_000 + tvmazeId (SeedTracked); resolve it rather than assume.
        var showId = 10_000_000 + 501;
        long[] watchEpisodeIds;
        await using (var db = CreateContext())
        {
            var episodeIds = db.Episodes.Where(e => e.ShowId == showId).Select(e => e.Id).ToList();
            foreach (var (epId, i) in episodeIds.Select((id, i) => (id, i)))
            {
                db.EpisodeWatches.Add(new EpisodeWatch
                {
                    Id = 9_000_000 + i,
                    ShowId = showId,
                    EpisodeId = epId,
                    WatchedAt = DateTimeOffset.UtcNow,
                });
            }

            await db.SaveChangesAsync();
            watchEpisodeIds = [.. episodeIds];
        }

        // Refresh again with one MORE episode. The mapper must add the new episode and never regenerate
        // the existing ids — episode_watch.episode_id points at them (SCHEMA §2a).
        source.With(501, "Running", episodes: 4);
        await RunJob(RefreshMode.EpisodeRefresh, source);

        await using (var db = CreateContext())
        {
            var marks = await db.EpisodeWatches.Where(w => w.ShowId == showId).ToListAsync();
            Assert.Equal(3, marks.Count);
            Assert.All(marks, m => Assert.Contains(m.EpisodeId, watchEpisodeIds));
        }
    }

    [Fact]
    public async Task Transport_failure_is_a_per_show_failure_not_a_crash()
    {
        SeedTracked(601, ShowStatus.Running);
        SeedTracked(602, ShowStatus.Running);

        var source = new FakeSource()
            .With(601, "Running", 1)
            .Unavailable(602); // TVmaze unreachable for this one show (R14)

        var report = await RunJob(RefreshMode.EpisodeRefresh, source);

        Assert.Equal(2, report.Total);
        Assert.Equal(1, report.Changed); // 601 mapped
        Assert.Equal(1, report.Failed);  // 602 left for the next run, not counted as "no change"
    }

    [Fact]
    public async Task Refuses_when_another_writer_holds_the_lease()
    {
        SeedTracked(701, ShowStatus.Running);
        var source = new FakeSource().With(701, "Running", 1);

        var report = await RunJob(RefreshMode.EpisodeRefresh, source, grantLease: false);

        Assert.True(report.Refused);
        Assert.Empty(source.RequestedIds); // refused before fetching anything
    }

    [Fact]
    public async Task Writes_a_job_run_row_with_total_and_changed()
    {
        SeedTracked(801, ShowStatus.Running);
        SeedTracked(802, ShowStatus.Running);
        var source = new FakeSource().With(801, "Running", 2).With(802, "Running", 2);

        await RunJob(RefreshMode.EpisodeRefresh, source);

        await using var db = CreateContext();
        var run = db.JobRuns.Single(r => r.JobName == "EpisodeRefresh");
        Assert.True(run.Succeeded);
        Assert.NotNull(run.FinishedAt);
        Assert.Equal(2, run.ItemsTotal);
        Assert.Equal(2, run.ItemsChanged);
    }

    // ---- helpers ----

    private async Task<RefreshReport> RunJob(
        RefreshMode mode, FakeSource source, bool grantLease = true)
    {
        await using var db = CreateContext();
        var mapper = new TvmazeShowMapper(
            db, _ids, NullLogger<TvmazeShowMapper>.Instance, source);
        var job = new ShowRefreshJob(
            db,
            new TrackedShowRepositoryProxy(db),
            mapper,
            new FakeLease(grantLease),
            _ids,
            new ShowsOptions(),
            NullLogger<ShowRefreshJob>.Instance);
        return await job.RunAsync(mode, CancellationToken.None);
    }

    private void SeedTracked(int tvmazeId, ShowStatus status, bool deleted = false)
    {
        using var db = CreateContext();

        // The NOT NULL tvmaze_show_id FK needs a source row to point at (SCHEMA §2); the mapper
        // overwrites it on the first map.
        db.TvmazeShows.Add(new TvmazeShow
        {
            TvmazeId = tvmazeId,
            StatusRaw = string.Empty,
            RawJson = "{}",
            FetchedAt = DateTimeOffset.UtcNow,
        });

        var show = new TrackedShow
        {
            Id = 10_000_000 + tvmazeId,
            TvmazeId = tvmazeId,
            Status = status,
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

    /// <summary>
    /// The repository the job depends on, wired to the same context the job writes through, so the
    /// mapper's in-place status update is visible to the job's old-status comparison.
    /// </summary>
    private sealed class TrackedShowRepositoryProxy(ShowsDbContext db) : ITrackedShowRepository
    {
        private readonly TrackedShowRepository _inner = new(db);

        public Task<IReadOnlyList<TrackedShow>> ListActiveByStatusAsync(
            IReadOnlyCollection<ShowStatus> statuses, CancellationToken cancellationToken) =>
            _inner.ListActiveByStatusAsync(statuses, cancellationToken);

        public Task<IReadOnlyList<TrackedShow>> ListActiveExcludingStatusAsync(
            IReadOnlyCollection<ShowStatus> statuses, CancellationToken cancellationToken) =>
            _inner.ListActiveExcludingStatusAsync(statuses, cancellationToken);

        // Unused by the refresh job; delegate to the real repository so the type stays honest.
        public Task<TrackedShow?> FindByIdAsync(long id, CancellationToken ct) => _inner.FindByIdAsync(id, ct);
        public Task<TrackedShow?> FindByIdWithGenresAsync(long id, CancellationToken ct) => _inner.FindByIdWithGenresAsync(id, ct);
        public Task<TrackedShow?> FindByTvmazeIdAsync(int id, CancellationToken ct) => _inner.FindByTvmazeIdAsync(id, ct);
        public Task<TrackedShow?> FindByTvmazeIdIncludingDeletedAsync(int id, CancellationToken ct) => _inner.FindByTvmazeIdIncludingDeletedAsync(id, ct);
        public Task<IReadOnlyList<TrackedShow>> ListActiveAsync(CancellationToken ct) => _inner.ListActiveAsync(ct);
        public Task<IReadOnlyList<TrackedShow>> ListActiveByIdsAsync(IReadOnlyList<long> ids, CancellationToken ct) => _inner.ListActiveByIdsAsync(ids, ct);
        public Task<IReadOnlyList<TrackedShow>> ListActiveByCategoryAsync(ShowCategory c, CancellationToken ct) => _inner.ListActiveByCategoryAsync(c, ct);
        public Task<IReadOnlyList<TrackedShow>> ListIncludingUntrackedAsync(CancellationToken ct) => _inner.ListIncludingUntrackedAsync(ct);
        public Task<Dictionary<int, long?>> GetActiveTrackingByTvmazeIdsAsync(IReadOnlyList<int> ids, CancellationToken ct) => _inner.GetActiveTrackingByTvmazeIdsAsync(ids, ct);
        public Task<IReadOnlyList<Season>> ListSeasonsByShowIdAsync(long showId, CancellationToken ct) => _inner.ListSeasonsByShowIdAsync(showId, ct);
        public Task<IReadOnlyList<Season>> ListSeasonsByShowIdsAsync(IReadOnlyList<long> showIds, CancellationToken ct) => _inner.ListSeasonsByShowIdsAsync(showIds, ct);
        public Task AddAsync(TrackedShow show, CancellationToken ct) => _inner.AddAsync(show, ct);
    }

    private sealed class FakeSource : ITvmazeShowSource
    {
        private readonly Dictionary<int, TvmazePayload?> _payloads = new();
        private readonly HashSet<int> _unavailable = [];
        public List<int> RequestedIds { get; } = [];

        public FakeSource With(int tvmazeId, string status, int episodes)
        {
            var show = new TvmazeShow
            {
                TvmazeId = tvmazeId,
                Name = $"Show {tvmazeId}",
                TypeRaw = "Scripted",
                StatusRaw = status,
                Premiered = "2015-01-01",
                GenresRaw = """["Drama"]""",
                RawJson = "{}",
                FetchedAt = DateTimeOffset.UtcNow,
            };

            var eps = new List<TvmazeEpisode>();
            for (var i = 1; i <= episodes; i++)
            {
                eps.Add(new TvmazeEpisode
                {
                    // Stable natural key per (show, season, number) so a re-run maps to the same episode.
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
            public string Name => "episode_refresh";
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
