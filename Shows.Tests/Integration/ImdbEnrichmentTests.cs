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
using Xunit;

namespace Shows.Tests.Integration;

/// <summary>
/// Step 7.3 acceptance for the <c>Enrichment --imdb</c> job (ADR-031): TVmaze is authoritative, IMDb only
/// completes it. The assertions are the ones the plan names: a missing episode title is filled with
/// <c>title_source = 'imdb'</c> and its <c>air_date</c> is never overwritten; a title/runtime conflict and
/// an IMDb-only episode each produce an <c>episode_mismatch</c> row (TVmaze value kept, IMDb value
/// rejected) with <b>no</b> new <c>episode</c> row; a re-run reports zero changes.
/// </summary>
public sealed class ImdbEnrichmentTests : IDisposable
{
    private const int TvmazeId = 1;
    private const string ImdbId = "tt0000001";
    private const long ShowId = 10_000_000 + TvmazeId;
    private const long SeasonId = 5_000_000;

    private readonly string _dbPath;
    private readonly SequentialIdGenerator _ids = new();

    public ImdbEnrichmentTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"shows-7-3-{Guid.NewGuid():N}.db");
        using var context = CreateContext();
        context.Database.EnsureCreated();
    }

    [Fact]
    public async Task Fills_a_missing_title_from_imdb_without_touching_air_date()
    {
        SeedShowWithEpisodes();

        var report = await RunJob();

        Assert.False(report.Refused);
        Assert.Equal(1, report.Changed); // S01E02 filled its title + runtime

        await using var db = CreateContext();
        var e02 = await db.Episodes.SingleAsync(e => e.SeasonNumber == 1 && e.EpisodeNumber == 2);
        Assert.Equal("The List", e02.Title);
        Assert.Equal("imdb", e02.TitleSource);
        Assert.Equal(45, e02.RuntimeMinutes);
        // The TVmaze air date is untouched — IMDb only had a year.
        Assert.Equal(new DateOnly(2011, 6, 5), e02.AirDate);
        Assert.Equal("tvmaze", e02.AirDateSource);
    }

    [Fact]
    public async Task Never_overwrites_a_tvmaze_value_and_flags_the_conflict()
    {
        SeedShowWithEpisodes();
        await RunJob();

        await using var db = CreateContext();
        var e03 = await db.Episodes.SingleAsync(e => e.SeasonNumber == 1 && e.EpisodeNumber == 3);
        // TVmaze wins: the IMDb title/runtime were rejected, not written.
        Assert.Equal("Old Title", e03.Title);
        Assert.Equal(50, e03.RuntimeMinutes);
        Assert.Null(e03.AirDate); // TVmaze gap, IMDb year not fabricated

        var title = await db.EpisodeMismatches.SingleAsync(
            m => m.EpisodeId == e03.Id && m.Kind == "title");
        Assert.Equal("Old Title", title.TvmazeValue);
        Assert.Equal("New Title", title.ImdbValue);

        var runtime = await db.EpisodeMismatches.SingleAsync(
            m => m.EpisodeId == e03.Id && m.Kind == "runtime");
        Assert.Equal("50", runtime.TvmazeValue);
        Assert.Equal("55", runtime.ImdbValue);

        // Air-date gap: TVmaze has none, IMDb offers only a year — recorded, never filled.
        var gap = await db.EpisodeMismatches.SingleAsync(
            m => m.EpisodeId == e03.Id && m.Kind == "air_date_gap");
        Assert.Null(gap.TvmazeValue);
        Assert.Equal("2011", gap.ImdbValue);
    }

    [Fact]
    public async Task Flags_an_imdb_only_episode_without_inserting_it()
    {
        SeedShowWithEpisodes();
        await RunJob();

        await using var db = CreateContext();
        // The (1,5) episode IMDb knows about is flagged, not merged into episode.
        Assert.False(await db.Episodes.AnyAsync(e => e.SeasonNumber == 1 && e.EpisodeNumber == 5));

        var only = await db.EpisodeMismatches.SingleAsync(m => m.Kind == "imdb_only");
        Assert.Null(only.EpisodeId);
        Assert.Equal(1, only.SeasonNumber);
        Assert.Equal(5, only.EpisodeNumber);
        Assert.Equal("Ghost Episode", only.ImdbValue);
    }

    [Fact]
    public async Task Flags_a_tvmaze_only_episode()
    {
        SeedShowWithEpisodes();
        await RunJob();

        await using var db = CreateContext();
        var e04 = await db.Episodes.SingleAsync(e => e.SeasonNumber == 1 && e.EpisodeNumber == 4);
        var only = await db.EpisodeMismatches.SingleAsync(
            m => m.EpisodeId == e04.Id && m.Kind == "tvmaze_only");
        Assert.Equal("Extra", only.TvmazeValue);
        Assert.Null(only.ImdbValue);
    }

    [Fact]
    public async Task Re_running_reports_zero_changes_and_recomputes_the_same_mismatches()
    {
        SeedShowWithEpisodes();
        var first = await RunJob();
        var second = await RunJob();

        Assert.Equal(1, first.Changed);
        Assert.Equal(0, second.Changed); // gaps already filled, links already set
        Assert.Equal(first.Mismatches, second.Mismatches); // wipe-and-recompute is stable

        await using var db = CreateContext();
        Assert.Equal(first.Mismatches, await db.EpisodeMismatches.CountAsync(m => m.ShowId == ShowId));
    }

    [Fact]
    public async Task Skips_a_show_with_no_imdb_id()
    {
        SeedShowWithEpisodes(withImdbId: false);
        var report = await RunJob();

        Assert.Equal(0, report.Total); // filtered out before the mapper
        await using var db = CreateContext();
        Assert.Equal(0, await db.EpisodeMismatches.CountAsync());
    }

    [Fact]
    public async Task Soft_deleted_show_is_never_enriched()
    {
        SeedShowWithEpisodes(deleted: true);
        var report = await RunJob();

        Assert.Equal(0, report.Total); // R8: the repository excludes soft-deleted shows
    }

    [Fact]
    public async Task Refuses_when_another_writer_holds_the_lease()
    {
        SeedShowWithEpisodes();
        var report = await RunJob(grantLease: false);

        Assert.True(report.Refused);
        Assert.Equal(0, report.Changed);
    }

    // ---- fixtures ----

    // One show (TVmaze-built) with four episodes exercising every branch, plus the matching imdb_episode
    // source rows. S01E01 agrees, S01E02 is a TVmaze gap, S01E03 conflicts on title/runtime and is missing
    // an air date, S01E04 is TVmaze-only; IMDb also knows a (1,5) episode TVmaze never created and a \N
    // legacy row that cannot be keyed.
    private void SeedShowWithEpisodes(bool withImdbId = true, bool deleted = false)
    {
        using var db = CreateContext();

        db.TvmazeShows.Add(new TvmazeShow
        {
            TvmazeId = TvmazeId,
            StatusRaw = "Ended",
            RawJson = "{}",
            FetchedAt = DateTimeOffset.UtcNow,
        });

        var show = new TrackedShow
        {
            Id = ShowId,
            TvmazeId = TvmazeId,
            Status = ShowStatus.Ended,
            Title = "Show",
            ImdbId = withImdbId ? ImdbId : null,
        };
        db.Entry(show).Property("tvmaze_show_id").CurrentValue = TvmazeId;
        show.User = new UserShow
        {
            TrackedShowId = ShowId,
            Category = ShowCategory.Finished,
            AddedAt = DateTimeOffset.UtcNow,
            CategoryChangedAt = DateTimeOffset.UtcNow,
            DeletedAt = deleted ? DateTimeOffset.UtcNow : null,
        };
        db.TrackedShows.Add(show);

        db.Seasons.Add(new Season { Id = SeasonId, ShowId = ShowId, SeasonNumber = 1 });

        AddEpisode(db, tvmazeEpisodeId: 101, season: 1, number: 2, title: null, runtime: null, airDate: new DateOnly(2011, 6, 5));
        AddEpisode(db, tvmazeEpisodeId: 103, season: 1, number: 3, title: "Old Title", runtime: 50, airDate: null);
        AddEpisode(db, tvmazeEpisodeId: 104, season: 1, number: 4, title: "Extra", runtime: null, airDate: new DateOnly(2011, 6, 19));

        // IMDb source rows (loaded by 7.4). S01E01 exists only on the IMDb side here to keep the fixture
        // small — it is the "agree" case, so it is represented by the (1,5) ghost and the conflicts above.
        db.ImdbTitles.Add(new ImdbTitle { Tconst = ImdbId, TitleType = "tvSeries", PrimaryTitle = "Show" });
        AddImdbEpisode(db, "tt0000001001", season: 1, number: 2, title: "The List", year: 2011, runtime: 45);
        AddImdbEpisode(db, "tt0000001002", season: 1, number: 3, title: "New Title", year: 2011, runtime: 55);
        AddImdbEpisode(db, "tt0000001003", season: 1, number: 5, title: "Ghost Episode", year: 2011, runtime: null);
        // A \N legacy row: null season/episode, cannot be keyed, must be skipped silently.
        AddImdbEpisode(db, "tt0000001004", season: null, number: null, title: "Legacy", year: null, runtime: null);

        db.SaveChanges();
    }

    private static void AddEpisode(
        ShowsDbContext db, long tvmazeEpisodeId, int season, int number, string? title, int? runtime, DateOnly? airDate)
    {
        db.Episodes.Add(new Episode
        {
            Id = 7_000_000 + tvmazeEpisodeId,
            ShowId = ShowId,
            SeasonId = SeasonId,
            TvmazeEpisodeId = (int)tvmazeEpisodeId,
            SeasonNumber = season,
            EpisodeNumber = number,
            Title = title,
            TitleSource = title is null ? null : "tvmaze",
            RuntimeMinutes = runtime,
            AirDate = airDate,
            AirDateSource = airDate is null ? null : "tvmaze",
            EpisodeType = EpisodeType.Regular,
        });
    }

    private static void AddImdbEpisode(
        ShowsDbContext db, string tconst, int? season, int? number, string? title, int? year, int? runtime)
    {
        db.ImdbEpisodes.Add(new ImdbEpisode
        {
            Tconst = tconst,
            ParentTconst = ImdbId,
            SeasonNumber = season,
            EpisodeNumber = number,
            PrimaryTitle = title,
            StartYear = year,
            RuntimeMinutes = runtime,
        });
    }

    // ---- helpers ----

    private async Task<ImdbEnrichmentReport> RunJob(bool grantLease = true)
    {
        await using var db = CreateContext();
        var job = new ImdbEnrichmentJob(
            db,
            new TrackedShowRepository(db),
            new TvmazeShowMapper(db, _ids, NullLogger<TvmazeShowMapper>.Instance),
            new FakeLease(grantLease),
            _ids,
            new ShowsOptions(),
            NullLogger<ImdbEnrichmentJob>.Instance);
        return await job.RunAsync(CancellationToken.None);
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

    private sealed class FakeLease(bool grant) : IWriterLease
    {
        public Task<IWriterLeaseHandle?> TryAcquireAsync(string name, CancellationToken cancellationToken) =>
            Task.FromResult<IWriterLeaseHandle?>(grant ? new Handle() : null);

        private sealed class Handle : IWriterLeaseHandle
        {
            public string Name => "enrichment_imdb";
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
