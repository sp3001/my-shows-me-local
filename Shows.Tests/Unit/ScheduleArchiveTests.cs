using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Shows.Application;
using Shows.Infrastructure.Discovery;
using Shows.Infrastructure.Persistence;

namespace Shows.Tests.Unit;

/// <summary>
/// The raw-snapshot archive that gives the Discovery window a past (user follow-up, 2026-10-07).
/// <c>/schedule/full</c> carries only today forward, so the look-back window is filled exclusively from
/// captures taken while those episodes were still ahead — which makes the retention rules the feature,
/// not the fetch. Every test drives an explicit <c>today</c>, so nothing depends on the wall clock, and
/// each runs against its own migrated SQLite database.
/// </summary>
public sealed class ScheduleArchiveTests : IDisposable
{
    private static readonly DateOnly Today = new(2026, 10, 7);
    private const int LayerDays = 14;

    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), $"shows-archive-{Guid.NewGuid():N}.db");
    private readonly DbContextOptions<ShowsDbContext> _options;
    private readonly TestContextFactory _contextFactory;

    public ScheduleArchiveTests()
    {
        _options = new DbContextOptionsBuilder<ShowsDbContext>()
            .UseSqlite(new SqliteConnectionStringBuilder { DataSource = _dbPath }.ToString())
            .UseSnakeCaseNamingConvention()
            .AddInterceptors(new SqlitePragmaInterceptor())
            .Options;
        _contextFactory = new TestContextFactory(_options);

        using var db = new ShowsDbContext(_options);
        db.Database.Migrate();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath))
        {
            File.Delete(_dbPath);
        }
    }

    // ---- promotion (the cascade) ----

    [Fact]
    public async Task ArchiveIfAged_stores_the_first_capture_when_nothing_is_retained()
    {
        var archive = Create();

        await archive.ArchiveIfAgedAsync("[]", Today, LayerDays, CancellationToken.None);

        var file = Assert.Single(await Create().ListNewestFirstAsync(CancellationToken.None));
        Assert.Equal(Today, file.FetchedOn);
        Assert.Equal("[]", await Create().ReadJsonAsync(file, CancellationToken.None));
    }

    [Fact]
    public async Task ArchiveIfAged_does_not_add_a_layer_while_layer_one_is_still_aging()
    {
        // Layer 1 is the AGING slot, not a 14-day-old one: a capture 13 days old has not yet earned
        // promotion, so a fetch today must not start a second layer.
        var archive = Create();
        await WriteCaptureAsync(Today.AddDays(-13), "old");

        await archive.ArchiveIfAgedAsync("current", Today, LayerDays, CancellationToken.None);

        Assert.Single(await archive.ListNewestFirstAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ArchiveIfAged_promotes_once_layer_one_reaches_the_interval()
    {
        // At exactly the interval the aging layer becomes layer 2 and today's response takes its place.
        var archive = Create();
        await WriteCaptureAsync(Today.AddDays(-LayerDays), "old");

        await archive.ArchiveIfAgedAsync("current", Today, LayerDays, CancellationToken.None);

        var files = await archive.ListNewestFirstAsync(CancellationToken.None);
        Assert.Equal(2, files.Count);
        Assert.Equal(Today, files[0].FetchedOn); // the new layer 1
        Assert.Equal(Today.AddDays(-LayerDays), files[1].FetchedOn); // the promoted layer 2
    }

    [Fact]
    public async Task ArchiveIfAged_overwrites_todays_capture_instead_of_duplicating_it()
    {
        // A same-day re-refresh must not pile up files under one name — the date IS the key.
        var archive = Create();
        await archive.ArchiveIfAgedAsync("first", Today, LayerDays, CancellationToken.None);

        await archive.ArchiveIfAgedAsync("second", Today, LayerDays, CancellationToken.None);

        var file = Assert.Single(await archive.ListNewestFirstAsync(CancellationToken.None));
        Assert.Equal("second", await archive.ReadJsonAsync(file, CancellationToken.None));
    }

    // ---- listing ----

    [Fact]
    public async Task ListNewestFirst_orders_by_capture_date()
    {
        await WriteCaptureAsync(Today.AddDays(-28), "a");
        await WriteCaptureAsync(Today, "b");
        await WriteCaptureAsync(Today.AddDays(-14), "c");

        var files = await Create().ListNewestFirstAsync(CancellationToken.None);

        Assert.Equal(
            new[] { Today, Today.AddDays(-14), Today.AddDays(-28) },
            files.Select(f => f.FetchedOn));
    }

    [Fact]
    public async Task ListNewestFirst_is_empty_when_no_captures_exist()
    {
        Assert.Empty(await Create().ListNewestFirstAsync(CancellationToken.None));
    }

    // ---- retention (its own ceiling, deliberately unrelated to the bucket window) ----

    [Fact]
    public async Task Prune_drops_exactly_what_has_passed_the_age_ceiling()
    {
        // Ceiling 60 from 2026-10-07 => keep from 2026-08-08. The 2026-08-07 capture is the only one past it.
        await WriteCaptureAsync(Today, "l1");
        await WriteCaptureAsync(Today.AddDays(-14), "l2");
        await WriteCaptureAsync(Today.AddDays(-48), "l3");
        await WriteCaptureAsync(Today.AddDays(-61), "expired");

        await Create().PruneAsync(Today, maxAgeDays: 60, CancellationToken.None);

        Assert.Equal(
            new[] { Today, Today.AddDays(-14), Today.AddDays(-48) },
            (await Create().ListNewestFirstAsync(CancellationToken.None)).Select(f => f.FetchedOn));
    }

    [Fact]
    public async Task Prune_ignores_the_bucket_window_entirely()
    {
        // The point of the separate knob: a 45-day window must not prune a 50-day-old layer. Coupling the
        // two would make widening the window silently destroy history.
        await WriteCaptureAsync(Today.AddDays(-50), "older than the window but inside the ceiling");

        await Create().PruneAsync(Today, maxAgeDays: 60, CancellationToken.None);

        Assert.Single(await Create().ListNewestFirstAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Prune_keeps_everything_when_the_ceiling_is_disabled()
    {
        await WriteCaptureAsync(Today.AddDays(-400), "ancient");

        await Create().PruneAsync(Today, maxAgeDays: 0, CancellationToken.None);

        Assert.Single(await Create().ListNewestFirstAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Prune_keeps_a_capture_sitting_exactly_on_the_ceiling()
    {
        // Boundary: 60 days old is the oldest still-kept age, not already expired.
        await WriteCaptureAsync(Today.AddDays(-60), "on the line");

        await Create().PruneAsync(Today, maxAgeDays: 60, CancellationToken.None);

        Assert.Single(await Create().ListNewestFirstAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Prune_is_a_no_op_with_no_captures()
    {
        await Create().PruneAsync(Today, maxAgeDays: 60, CancellationToken.None);

        Assert.Empty(await Create().ListNewestFirstAsync(CancellationToken.None));
    }

    // ---- helpers ----

    private ScheduleArchive Create() =>
        new(_contextFactory, NullLogger<ScheduleArchive>.Instance);

    private async Task WriteCaptureAsync(DateOnly fetchedOn, string json)
    {
        await using var db = await _contextFactory.CreateDbContextAsync();
        db.DiscoveryScheduleArchives.Add(new DiscoveryScheduleArchiveEntry
        {
            FetchedOn = fetchedOn,
            RawJson = json,
        });
        await db.SaveChangesAsync();
    }

    private sealed class TestContextFactory(DbContextOptions<ShowsDbContext> options)
        : IDbContextFactory<ShowsDbContext>
    {
        public ShowsDbContext CreateDbContext() => new(options);

        public Task<ShowsDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }

    // ---- the merge, driven through the real parser ----

    [Fact]
    public void Merge_recovers_a_show_that_today_schedule_dropped_entirely()
    {
        // The whole point of the archive: a show whose season-1 run finished is simply absent from a
        // forward-only schedule. Without the old capture it cannot appear in the New bucket at all.
        var oldLayer = Parse(
            Episode(1, 1, "2026-09-05", Show(1, "Already Aired")),
            Episode(1, 2, "2026-09-12", Show(1, "Already Aired")));
        var current = Parse(Episode(1, 1, "2026-10-20", Show(2, "Upcoming One")));

        var merged = ScheduleMerge.Merge(current, [oldLayer]);

        Assert.Equal(2, merged.Count);
        var recovered = Assert.Single(merged, e => e.TvmazeId == 1);
        Assert.Equal(2, recovered.Episodes.Count);
        Assert.Equal("Already Aired", recovered.Show.Title);
    }

    [Fact]
    public void Merge_takes_the_newer_air_date_when_an_episode_moved()
    {
        // TVmaze reschedules episodes. The current response is authoritative, so keeping the old slot
        // would resurrect a date the network has already abandoned.
        var oldLayer = Parse(Episode(2, 1, "2026-11-05", Show(1, "Moved")));
        var current = Parse(Episode(2, 1, "2026-11-19", Show(1, "Moved")));

        var merged = ScheduleMerge.Merge(current, [oldLayer]);

        var episode = Assert.Single(Assert.Single(merged).Episodes);
        Assert.Equal(new DateOnly(2026, 11, 19), episode.AirDate);
    }

    [Fact]
    public void Merge_keeps_an_older_air_date_when_the_newer_layer_carries_none()
    {
        // A missing date in the newer layer is an omission, not a withdrawal — losing a known date to a
        // parsing gap would be the worse error.
        var oldLayer = Parse(Episode(1, 3, "2026-11-30", Show(1, "Gap In Newer")));
        var current = Parse(Episode(1, 3, null, Show(1, "Gap In Newer")));

        var merged = ScheduleMerge.Merge(current, [oldLayer]);

        var episode = Assert.Single(Assert.Single(merged).Episodes);
        Assert.Equal(new DateOnly(2026, 11, 30), episode.AirDate);
    }

    [Fact]
    public void Merge_takes_show_metadata_from_the_current_layer()
    {
        var oldLayer = Parse(Episode(1, 1, "2026-09-05", Show(1, "Old Title", network: "Old Net")));
        var current = Parse(Episode(1, 1, "2026-10-20", Show(1, "New Title", network: "New Net")));

        var merged = ScheduleMerge.Merge(current, [oldLayer]);

        var show = Assert.Single(merged);
        Assert.Equal("New Title", show.Show.Title);
        Assert.Equal("New Net", show.NetworkName);
    }

    [Fact]
    public void Merge_returns_the_current_snapshot_untouched_when_there_are_no_layers()
    {
        var current = Parse(Episode(1, 1, "2026-10-20", Show(1, "Only")));

        var merged = ScheduleMerge.Merge(current, []);

        Assert.Same(current, merged);
    }

    [Fact]
    public void Merge_applies_layers_oldest_first_so_a_newer_correction_wins()
    {
        // Three layers disagree on one episode's date. The newest layer that carries a date must win,
        // which only holds if the layers are applied oldest-to-newest.
        var oldest = Parse(Episode(1, 1, "2026-11-01", Show(1, "Drifting")));
        var middle = Parse(Episode(1, 1, "2026-11-08", Show(1, "Drifting")));
        var current = Parse(Episode(1, 1, "2026-11-15", Show(1, "Drifting")));

        var merged = ScheduleMerge.Merge(current, [middle, oldest]); // newest-first, as the source supplies

        var episode = Assert.Single(Assert.Single(merged).Episodes);
        Assert.Equal(new DateOnly(2026, 11, 15), episode.AirDate);
    }

    [Fact]
    public void Merged_snapshot_puts_a_finished_first_season_in_the_New_bucket()
    {
        // End-to-end through the real index: today's schedule has no past episodes, so the show is only
        // New because the archived layer still carries its season-1 dates inside the window. The control
        // show is a season-2 premiere — under the no-upper-bound New rule a future S01E01 would itself
        // count as New, which would make this assertion prove nothing.
        var oldLayer = Parse(Episode(1, 1, "2026-09-10", Show(1, "Short Lived")));
        var current = Parse(Episode(2, 1, "2026-12-01", Show(2, "Returning Later")));
        var merged = ScheduleMerge.Merge(current, [oldLayer]);

        var page = DiscoveryIndex.Query(
            merged, DiscoveryBucket.New, new DiscoveryFilter(), 1, 30, Today, windowDays: 45);

        var only = Assert.Single(page.Items);
        Assert.Equal("Short Lived", only.Title);
    }

    private static IReadOnlyList<DiscoveryEntry> Parse(params string[] episodes) =>
        ScheduleParser.Parse("[" + string.Join(",", episodes) + "]");

    private static string Episode(int season, int number, string? airdate, string show) =>
        "{\"season\":" + season + ",\"number\":" + number
        + ",\"airdate\":" + (airdate is null ? "null" : "\"" + airdate + "\"")
        + ",\"type\":\"regular\",\"_embedded\":{\"show\":" + show + "}}";

    private static string Show(int id, string name, string? network = null)
    {
        var net = network is null
            ? "null"
            : "{\"name\":\"" + network + "\",\"country\":{\"name\":\"United States\",\"code\":\"US\"}}";
        return "{\"id\":" + id + ",\"name\":\"" + name + "\",\"genres\":[\"Drama\"],"
            + "\"status\":\"Running\",\"type\":\"Scripted\",\"network\":" + net + ",\"webChannel\":null}";
    }
}
