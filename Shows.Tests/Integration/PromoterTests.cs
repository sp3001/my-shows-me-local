using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Shows.Application;
using Shows.Domain;
using Shows.Infrastructure.Configuration;
using Shows.Infrastructure.Import;
using Shows.Infrastructure.Persistence;
using Shows.Infrastructure.Persistence.Sources;
using Shows.Infrastructure.Persistence.Staging;
using Xunit;

namespace Shows.Tests.Integration;

/// <summary>
/// Step 2.6 acceptance for <see cref="Promoter"/> — the second phase of the two-phase import, the only
/// path that writes <c>user_show</c> and <c>episode_watch</c> for a whole library. Driven by a fake
/// <see cref="ITvmazeShowSource"/> over synthetic payloads, over a real migrated database (the view the
/// gate reads is hand-written SQL, so <c>Migrate()</c> not <c>EnsureCreated()</c>) — no network. The
/// assertions are the ones that fail silently when wrong: a special auto-marked as watched, a fully-watched
/// show flagged for review, a re-run that re-mints ids and orphans marks, or a promote that runs past a
/// blocking row or a live foreign lease.
/// </summary>
public sealed class PromoterTests : IDisposable
{
    private readonly string _dbPath;

    public PromoterTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"shows-2-6-{Guid.NewGuid():N}.db");
        using var context = CreateContext();
        context.Database.Migrate();
    }

    [Fact]
    public async Task Fully_watched_show_marks_every_regular_and_is_not_flagged_for_review()
    {
        // Branch 3 (watched == total): unambiguous, all regulars watched, needs_review = 0. This is the
        // 33-show case in DATA_ANALYSIS §5 — the branch order (total before the watched>0 heuristic) is
        // what keeps these un-flagged; literal doc order would flag all 33.
        SeedShow(myshows: 1, tvmaze: 101, status: "watching", total: 5, watched: 5);
        var source = new FakeSource().Add(Payload(101, regulars: 5, status: "Ended"));

        var report = await RunPromoter(source);

        Assert.False(report.Refused);
        Assert.Equal(1, report.Promoted);

        await using var db = CreateContext();
        var show = await db.TrackedShows.SingleAsync(s => s.TvmazeId == 101);
        Assert.Equal(5, show.TotalRegular);
        Assert.Equal(5, show.WatchedRegular);
        Assert.True(show.AutoFinishEligible); // all regulars watched + Ended → SPEC R5

        var user = await db.UserShows.SingleAsync(u => u.TrackedShowId == show.Id);
        Assert.False(user.NeedsReview);
        Assert.Equal(ShowCategory.Started, user.Category); // watching → Started

        Assert.Equal(5, await db.EpisodeWatches.CountAsync(w => w.ShowId == show.Id));
    }

    [Fact]
    public async Task Insignificant_specials_are_never_auto_marked_even_when_every_regular_is_watched()
    {
        // SPEC R4 / DATA_ANALYSIS §5 / ADR-034: insignificant specials are excluded from progress and
        // never marked in any branch. The fixture's "special" type maps to InsignificantSpecial.
        SeedShow(myshows: 1, tvmaze: 101, status: "watching", total: 2, watched: 2);
        var source = new FakeSource().Add(Payload(101, regulars: 2, status: "Ended", withSpecial: true));

        await RunPromoter(source);

        await using var db = CreateContext();
        var show = await db.TrackedShows.SingleAsync(s => s.TvmazeId == 101);
        var special = await db.Episodes.SingleAsync(
            e => e.ShowId == show.Id && e.EpisodeType == EpisodeType.InsignificantSpecial);
        Assert.False(await db.EpisodeWatches.AnyAsync(w => w.EpisodeId == special.Id));
        Assert.True(show.HasUnwatchedSpecials); // the special survives unwatched and is surfaced
    }

    [Fact]
    public async Task Explicit_unwatched_list_marks_every_regular_before_the_earliest_gap()
    {
        // Branch 1: staging_episode is the UNWATCHED set. Listing s01e04 and s01e05 as unwatched means
        // e01-e03 are watched. watchedEpisodes (3) matches → needs_review = 0.
        SeedShow(myshows: 1, tvmaze: 101, status: "watching", total: 5, watched: 3);
        SeedUnwatched(1, season: 1, episode: 4);
        SeedUnwatched(1, season: 1, episode: 5);
        var source = new FakeSource().Add(Payload(101, regulars: 5, status: "Running"));

        await RunPromoter(source);

        await using var db = CreateContext();
        var show = await db.TrackedShows.SingleAsync(s => s.TvmazeId == 101);
        Assert.Equal(3, show.WatchedRegular);
        Assert.Equal(5, show.TotalRegular);

        var watchedNumbers = await db.EpisodeWatches
            .Where(w => w.ShowId == show.Id)
            .Join(db.Episodes, w => w.EpisodeId, e => e.Id, (w, e) => e.EpisodeNumber)
            .ToListAsync();
        Assert.Equal([1, 2, 3], watchedNumbers.Order().ToList());

        var user = await db.UserShows.SingleAsync(u => u.TrackedShowId == show.Id);
        Assert.False(user.NeedsReview); // count matched watchedEpisodes
    }

    [Fact]
    public async Task Explicit_list_that_disagrees_with_the_count_is_flagged_for_review()
    {
        // Branch 1 with a mismatch: the list implies 3 watched but watchedEpisodes says 2 → needs_review.
        SeedShow(myshows: 1, tvmaze: 101, status: "watching", total: 5, watched: 2);
        SeedUnwatched(1, season: 1, episode: 4);
        SeedUnwatched(1, season: 1, episode: 5);
        var source = new FakeSource().Add(Payload(101, regulars: 5, status: "Running"));

        await RunPromoter(source);

        await using var db = CreateContext();
        var show = await db.TrackedShows.SingleAsync(s => s.TvmazeId == 101);
        var user = await db.UserShows.SingleAsync(u => u.TrackedShowId == show.Id);
        Assert.True(user.NeedsReview);
    }

    [Fact]
    public async Task No_list_partial_watch_uses_the_first_N_heuristic_and_is_always_reviewed()
    {
        // Branch 2: no staging_episode rows, watched (2) < total (5) → first 2 regulars, needs_review = 1.
        SeedShow(myshows: 1, tvmaze: 101, status: "watching", total: 5, watched: 2);
        var source = new FakeSource().Add(Payload(101, regulars: 5, status: "Running"));

        await RunPromoter(source);

        await using var db = CreateContext();
        var show = await db.TrackedShows.SingleAsync(s => s.TvmazeId == 101);
        Assert.Equal(2, show.WatchedRegular);

        var watchedNumbers = await db.EpisodeWatches
            .Where(w => w.ShowId == show.Id)
            .Join(db.Episodes, w => w.EpisodeId, e => e.Id, (w, e) => e.EpisodeNumber)
            .ToListAsync();
        Assert.Equal([1, 2], watchedNumbers.Order().ToList());

        var user = await db.UserShows.SingleAsync(u => u.TrackedShowId == show.Id);
        Assert.True(user.NeedsReview);
    }

    [Fact]
    public async Task Zero_total_episodes_promotes_to_an_empty_show_not_eligible()
    {
        // The edge case that must not crash (DATA_ANALYSIS §5, SPEC R7): total=0 → total_regular=0,
        // auto_finish_eligible=0. It is a warning in the gate, not a blocker, so it still promotes.
        SeedShow(myshows: 1, tvmaze: 101, status: "later", total: 0, watched: 0);
        var source = new FakeSource().Add(Payload(101, regulars: 0, status: "Ended"));

        var report = await RunPromoter(source);

        Assert.False(report.Refused);
        Assert.Equal(1, report.Promoted);

        await using var db = CreateContext();
        var show = await db.TrackedShows.SingleAsync(s => s.TvmazeId == 101);
        Assert.Equal(0, show.TotalRegular);
        Assert.Equal(0, show.WatchedRegular);
        Assert.False(show.AutoFinishEligible);
    }

    [Fact]
    public async Task Each_source_status_maps_to_its_category()
    {
        SeedShow(myshows: 1, tvmaze: 101, status: "watching", total: 1, watched: 1);
        SeedShow(myshows: 2, tvmaze: 102, status: "later", total: 1, watched: 1);
        SeedShow(myshows: 3, tvmaze: 103, status: "cancelled", total: 1, watched: 1);
        SeedShow(myshows: 4, tvmaze: 104, status: "finished", total: 1, watched: 1);
        var source = new FakeSource()
            .Add(Payload(101, 1)).Add(Payload(102, 1)).Add(Payload(103, 1)).Add(Payload(104, 1));

        var report = await RunPromoter(source);

        Assert.Equal(4, report.Promoted);
        await using var db = CreateContext();
        Assert.Equal(ShowCategory.Started, CategoryOf(db, 101));
        Assert.Equal(ShowCategory.Later, CategoryOf(db, 102));
        Assert.Equal(ShowCategory.Cancelled, CategoryOf(db, 103));
        Assert.Equal(ShowCategory.Finished, CategoryOf(db, 104));
    }

    [Fact]
    public async Task Every_promoted_show_gets_exactly_one_user_show()
    {
        // SPEC P4: a tracked_show without a user_show is an invisible show. The counts must match.
        SeedShow(myshows: 1, tvmaze: 101, status: "watching", total: 2, watched: 2);
        SeedShow(myshows: 2, tvmaze: 102, status: "later", total: 3, watched: 0);
        var source = new FakeSource().Add(Payload(101, 2)).Add(Payload(102, 3));

        await RunPromoter(source);

        await using var db = CreateContext();
        Assert.Equal(await db.TrackedShows.CountAsync(), await db.UserShows.CountAsync());
    }

    [Fact]
    public async Task Re_running_promotes_nothing_and_changes_no_watch_mark()
    {
        // Idempotence across runs: promoted_at IS NOT NULL removes a show from the target set, so its
        // episode ids are never re-minted and its marks are never touched.
        SeedShow(myshows: 1, tvmaze: 101, status: "watching", total: 3, watched: 3);
        var source = new FakeSource().Add(Payload(101, 3, status: "Ended"));

        await RunPromoter(source);

        await using var db = CreateContext();
        var showId = (await db.TrackedShows.SingleAsync(s => s.TvmazeId == 101)).Id;
        var requestedAfterFirst = source.Requested.Count;
        var watchIds = await db.EpisodeWatches
            .Where(w => w.ShowId == showId)
            .OrderBy(w => w.Id)
            .Select(w => w.Id)
            .ToListAsync();

        var second = await RunPromoter(source);

        Assert.Equal(0, second.Promoted); // nothing targeted
        Assert.Empty(source.Requested.Skip(requestedAfterFirst)); // the second run fetched nothing

        await using var db2 = CreateContext();
        var watchIdsAfter = await db2.EpisodeWatches
            .Where(w => w.ShowId == showId)
            .OrderBy(w => w.Id)
            .Select(w => w.Id)
            .ToListAsync();
        Assert.Equal(watchIds, watchIdsAfter); // marks intact, ids not regenerated
    }

    [Fact]
    public async Task Refuses_and_writes_nothing_when_a_blocking_row_remains()
    {
        // The review gate (SCHEMA §6): an unresolved tvmaze_id is blocking. Nothing may be promoted.
        SeedShow(myshows: 1, tvmaze: 101, status: "watching", total: 2, watched: 2);
        SeedShow(myshows: 2, tvmaze: null, status: "later", total: 2, watched: 0); // blocking
        var source = new FakeSource().Add(Payload(101, 2));

        var report = await RunPromoter(source);

        Assert.True(report.Refused);
        Assert.Equal(0, report.Promoted);
        Assert.Empty(source.Requested);

        await using var db = CreateContext();
        Assert.Equal(0, await db.TrackedShows.CountAsync());
        Assert.Equal(0, await db.UserShows.CountAsync());
    }

    [Fact]
    public async Task Refuses_when_a_foreign_writer_lease_is_live()
    {
        // ADR-028: --promote is exclusive of every other bulk job. A live foreign lease stops it before
        // any write, even though its own 'promote' lease was granted.
        SeedShow(myshows: 1, tvmaze: 101, status: "watching", total: 2, watched: 2);
        SeedLease("episode_refresh", DateTimeOffset.UtcNow.AddHours(1)); // live
        var source = new FakeSource().Add(Payload(101, 2));

        var report = await RunPromoter(source);

        Assert.True(report.Refused);
        Assert.Equal(0, report.Promoted);
        await using var db = CreateContext();
        Assert.Equal(0, await db.TrackedShows.CountAsync());
    }

    [Fact]
    public async Task A_stale_foreign_lease_does_not_block_promotion()
    {
        // Expiry is the cleanup mechanism (ADR-028): a lease whose expires_at is in the past is dead, so
        // it must not refuse a promote. Proves the liveness comparison is real, not "any row blocks".
        SeedShow(myshows: 1, tvmaze: 101, status: "watching", total: 2, watched: 2);
        SeedLease("episode_refresh", DateTimeOffset.UtcNow.AddHours(-1)); // stale
        var source = new FakeSource().Add(Payload(101, 2));

        var report = await RunPromoter(source);

        Assert.False(report.Refused);
        Assert.Equal(1, report.Promoted);
    }

    // ---- helpers ----

    private static ShowCategory CategoryOf(ShowsDbContext db, int tvmazeId) =>
        db.UserShows.Single(u => u.Show!.TvmazeId == tvmazeId).Category;

    private void SeedShow(
        int myshows, int? tvmaze, string status, int total, int watched)
    {
        using var db = CreateContext();
        db.StagingShows.Add(new StagingShow
        {
            MyshowsId = myshows,
            TvmazeId = tvmaze,
            TvmazeResolution = tvmaze is null ? "unresolved" : "scraped",
            SourceFile = status + ".json",
            SourceWatchStatus = status,
            TotalEpisodes = total,
            WatchedEpisodes = watched,
        });
        db.SaveChanges();
    }

    private void SeedUnwatched(int myshows, int season, int episode)
    {
        using var db = CreateContext();
        db.StagingEpisodes.Add(new StagingEpisode
        {
            ShowMyshowsId = myshows,
            SeasonNumber = season,
            EpisodeNumber = episode,
            SourceBatchId = 58,
            EpisodeIdSrc = season * 1000 + episode,
        });
        db.SaveChanges();
    }

    private void SeedLease(string name, DateTimeOffset expiresAt)
    {
        using var db = CreateContext();
        db.WriterLeases.Add(new WriterLease
        {
            Name = name,
            Holder = "test-holder",
            AcquiredAt = expiresAt.AddHours(-1),
            ExpiresAt = expiresAt,
        });
        db.SaveChanges();
    }

    private async Task<PromoteReport> RunPromoter(
        FakeSource source, bool grantLease = true)
    {
        using var db = CreateContext();
        var ids = new SequentialIdGenerator();
        var mapper = new TvmazeShowMapper(db, ids, NullLogger<TvmazeShowMapper>.Instance, source);
        var promoter = new Promoter(
            db, mapper, source, ids, new FakeLease(grantLease), new ShowsOptions(),
            NullLogger<Promoter>.Instance);
        return await promoter.RunAsync(CancellationToken.None);
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

    // A synthetic payload: `regulars` regular episodes in season 1 (numbered 1..N), optionally one
    // special in season 0. Status controls auto_finish_eligible via the mapper's vocabulary.
    private static TvmazePayload Payload(int tvmazeId, int regulars, string status = "Ended", bool withSpecial = false)
    {
        var show = new TvmazeShow
        {
            TvmazeId = tvmazeId,
            Name = $"Show {tvmazeId}",
            TypeRaw = "Scripted",
            StatusRaw = status,
            RawJson = "{}",
            FetchedAt = DateTimeOffset.UtcNow,
        };

        var episodes = new List<TvmazeEpisode>();
        for (var n = 1; n <= regulars; n++)
        {
            episodes.Add(new TvmazeEpisode
            {
                TvmazeEpisodeId = tvmazeId * 1000 + n,
                TvmazeShowId = tvmazeId,
                Name = $"S01E{n:D2}",
                SeasonNumber = 1,
                Number = n,
                TypeRaw = "regular",
                Airdate = "2020-01-01",
                Airstamp = $"2020-01-0{n + 1}T21:00:00+00:00",
                RawJson = "{}",
                FetchedAt = DateTimeOffset.UtcNow,
            });
        }

        if (withSpecial)
        {
            episodes.Add(new TvmazeEpisode
            {
                TvmazeEpisodeId = tvmazeId * 1000 + 900,
                TvmazeShowId = tvmazeId,
                Name = "Special",
                SeasonNumber = 0,
                Number = 1,
                TypeRaw = "special",
                Airdate = "2021-12-16",
                Airstamp = "2021-12-16T00:00:00+00:00",
                RawJson = "{}",
                FetchedAt = DateTimeOffset.UtcNow,
            });
        }

        return new TvmazePayload(show, episodes);
    }

    private sealed class FakeSource : ITvmazeShowSource
    {
        private readonly Dictionary<int, TvmazePayload> _byId = [];

        public List<int> Requested { get; } = [];

        public FakeSource Add(TvmazePayload payload)
        {
            _byId[payload.Show.TvmazeId] = payload;
            return this;
        }

        public Task<TvmazePayload?> FetchAsync(int tvmazeId, CancellationToken cancellationToken)
        {
            Requested.Add(tvmazeId);
            return Task.FromResult(_byId.TryGetValue(tvmazeId, out var p) ? p : null);
        }
    }

    private sealed class FakeLease(bool grant) : IWriterLease
    {
        public Task<IWriterLeaseHandle?> TryAcquireAsync(string name, CancellationToken cancellationToken) =>
            Task.FromResult<IWriterLeaseHandle?>(grant ? new Handle() : null);

        private sealed class Handle : IWriterLeaseHandle
        {
            public string Name => "promote";
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
