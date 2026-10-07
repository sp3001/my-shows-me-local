using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Shows.Application;
using Shows.Application.UseCases;
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
/// Step 7.5 acceptance for the <c>Enrichment --akas</c> job and the details-page button's use case, driven
/// by a fake <see cref="ICatalogProvider"/> so no network is touched. The assertions are the ones the plan
/// names: show 59985 gets the first RU aka, a show with no RU aka stays null (never a wrong-country guess),
/// a re-run reports zero changes, and the button lists every alternate.
/// </summary>
public sealed class AkasJobTests : IDisposable
{
    // The probed live fixture (DATA_ANALYSIS §8 / step 7.5): TVmaze 59985 returns three RU + two CN akas.
    private const int AkasShowTvmazeId = 59985;

    private readonly string _dbPath;
    private readonly SequentialIdGenerator _ids = new();

    public AkasJobTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"shows-7-5-{Guid.NewGuid():N}.db");
        using var context = CreateContext();
        context.Database.EnsureCreated();
    }

    [Fact]
    public async Task First_ru_aka_becomes_the_localized_title_and_all_akas_are_stored()
    {
        SeedTracked(AkasShowTvmazeId);
        var provider = new FakeProvider().With(AkasShowTvmazeId, RuAkas());

        var report = await RunJob(provider);

        Assert.False(report.Refused);
        Assert.Equal(1, report.Changed);

        await using var db = CreateContext();
        var show = db.TrackedShows.Single(s => s.TvmazeId == AkasShowTvmazeId);
        Assert.Equal("Начало", show.TitleLocalized); // first RU, by array order

        // All five akas are stored verbatim, with aka_order preserving the API order.
        var akas = await db.TvmazeAkas.Where(a => a.TvmazeShowId == AkasShowTvmazeId)
            .OrderBy(a => a.AkaOrder).ToListAsync();
        Assert.Equal(5, akas.Count);
        Assert.Equal(["RU", "RU", "RU", "CN", "CN"], akas.Select(a => a.CountryCode).ToArray());
        Assert.Equal("Начало", akas[0].Name);
        Assert.Equal(0, akas[0].AkaOrder);
    }

    [Fact]
    public async Task A_show_with_no_ru_aka_leaves_the_localized_title_null()
    {
        SeedTracked(111);
        // Only CN akas — no RU match. The pick must be null, not a wrong-country guess.
        var provider = new FakeProvider().With(111,
        [
            new ShowAka("Chinese One", "CN", "China"),
            new ShowAka("Chinese Two", "CN", "China"),
        ]);

        await RunJob(provider);

        await using var db = CreateContext();
        Assert.Null(db.TrackedShows.Single(s => s.TvmazeId == 111).TitleLocalized);
    }

    [Fact]
    public async Task Re_running_reports_zero_changes()
    {
        SeedTracked(AkasShowTvmazeId);
        var provider = new FakeProvider().With(AkasShowTvmazeId, RuAkas());

        await RunJob(provider);
        var second = await RunJob(provider);

        // Same akas, same pick -> the mapped title did not change. items_changed is the mapped-title
        // change, not source-row churn.
        Assert.Equal(0, second.Changed);
    }

    [Fact]
    public async Task Soft_deleted_show_is_never_fetched()
    {
        SeedTracked(AkasShowTvmazeId, deleted: true);
        SeedTracked(222);
        var provider = new FakeProvider().With(AkasShowTvmazeId, RuAkas()).With(222, RuAkas());

        await RunJob(provider);

        Assert.Equal([222], provider.RequestedIds.ToArray()); // R8: the soft-deleted show is skipped
    }

    [Fact]
    public async Task Transport_failure_is_a_per_show_failure_not_a_crash()
    {
        SeedTracked(301);
        SeedTracked(302);
        var provider = new FakeProvider().With(301, RuAkas()).Unavailable(302);

        var report = await RunJob(provider);

        Assert.Equal(2, report.Total);
        Assert.Equal(1, report.Changed);
        Assert.Equal(1, report.Failed); // R14: left for the next run, not counted as no-change
    }

    [Fact]
    public async Task Refuses_when_another_writer_holds_the_lease()
    {
        SeedTracked(AkasShowTvmazeId);
        var provider = new FakeProvider().With(AkasShowTvmazeId, RuAkas());

        var report = await RunJob(provider, grantLease: false);

        Assert.True(report.Refused);
        Assert.Empty(provider.RequestedIds);
    }

    [Fact]
    public async Task Button_use_case_lists_every_aka_and_sets_the_same_title()
    {
        SeedTracked(AkasShowTvmazeId);
        var provider = new FakeProvider().With(AkasShowTvmazeId, RuAkas());
        long trackedShowId = 10_000_000 + AkasShowTvmazeId;

        await using (var db = CreateContext())
        {
            var useCase = new FetchLocalizedTitlesUseCase(
                new TrackedShowRepository(db),
                provider,
                new TvmazeShowMapper(db, _ids, NullLogger<TvmazeShowMapper>.Instance),
                new UnitOfWork(db),
                "RU");

            var result = await useCase.ExecuteAsync(trackedShowId, CancellationToken.None);

            // The button lists all five alternates and reports the same pick the job would set.
            Assert.Equal(5, result.Akas.Count);
            Assert.Equal("Начало", result.PickedTitle);
        }

        await using (var db = CreateContext())
        {
            Assert.Equal("Начало", db.TrackedShows.Single(s => s.Id == trackedShowId).TitleLocalized);
        }
    }

    // ---- helpers ----

    private static IReadOnlyList<ShowAka> RuAkas() =>
    [
        new ShowAka("Начало", "RU", "Russian Federation"),
        new ShowAka("Сначала", "RU", "Russian Federation"),
        new ShowAka("Перезагрузка", "RU", "Russian Federation"),
        new ShowAka("Black Mirror CN 1", "CN", "China"),
        new ShowAka("Black Mirror CN 2", "CN", "China"),
    ];

    private async Task<AkasReport> RunJob(FakeProvider provider, bool grantLease = true)
    {
        await using var db = CreateContext();
        var job = new AkasJob(
            db,
            new TrackedShowRepository(db),
            provider,
            new TvmazeShowMapper(db, _ids, NullLogger<TvmazeShowMapper>.Instance),
            new FakeLease(grantLease),
            _ids,
            new ShowsOptions(),
            NullLogger<AkasJob>.Instance);
        return await job.RunAsync(CancellationToken.None);
    }

    private void SeedTracked(int tvmazeId, bool deleted = false)
    {
        using var db = CreateContext();

        db.TvmazeShows.Add(new TvmazeShow
        {
            TvmazeId = tvmazeId,
            StatusRaw = "Ended",
            RawJson = "{}",
            FetchedAt = DateTimeOffset.UtcNow,
        });

        var show = new TrackedShow
        {
            Id = 10_000_000 + tvmazeId,
            TvmazeId = tvmazeId,
            Status = ShowStatus.Ended,
            Title = $"Show {tvmazeId}",
        };
        db.Entry(show).Property("tvmaze_show_id").CurrentValue = tvmazeId;
        show.User = new UserShow
        {
            TrackedShowId = show.Id,
            Category = ShowCategory.Finished,
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

    private sealed class FakeProvider : ICatalogProvider
    {
        private readonly Dictionary<int, IReadOnlyList<ShowAka>> _akases = new();
        private readonly HashSet<int> _unavailable = [];
        public List<int> RequestedIds { get; } = [];

        public FakeProvider With(int tvmazeId, IReadOnlyList<ShowAka> akas)
        {
            _akases[tvmazeId] = akas;
            return this;
        }

        public FakeProvider Unavailable(int tvmazeId)
        {
            _unavailable.Add(tvmazeId);
            return this;
        }

        public Task<IReadOnlyList<ShowAka>> GetAkasAsync(int tvmazeId, CancellationToken cancellationToken)
        {
            RequestedIds.Add(tvmazeId);
            if (_unavailable.Contains(tvmazeId))
            {
                throw new TvmazeUnavailableException("tvmaze.example unreachable", new IOException("reset"));
            }

            return Task.FromResult(_akases.TryGetValue(tvmazeId, out var a)
                ? a
                : (IReadOnlyList<ShowAka>)[]);
        }

        // Unused by the akas job.
        public Task<IReadOnlyList<CatalogShow>> SearchAsync(string q, CancellationToken ct) => throw new NotSupportedException();
        public Task<CatalogShow?> GetShowAsync(int id, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<EpisodeCatalogInfo>> GetEpisodesAsync(int id, CancellationToken ct) => throw new NotSupportedException();
        public Task<int?> LookupByImdbIdAsync(string imdb, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<CatalogShow>> GetNewShowsAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<CatalogShow>> GetUpcomingAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyDictionary<int, long>> GetShowUpdateTimestampsAsync(CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class FakeLease(bool grant) : IWriterLease
    {
        public Task<IWriterLeaseHandle?> TryAcquireAsync(string name, CancellationToken cancellationToken) =>
            Task.FromResult<IWriterLeaseHandle?>(grant ? new Handle() : null);

        private sealed class Handle : IWriterLeaseHandle
        {
            public string Name => "enrichment_akas";
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
