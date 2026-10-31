using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Shows.Domain;
using Shows.Infrastructure.Persistence;
using Shows.Infrastructure.Persistence.Sources;
using Xunit;

namespace Shows.Tests.Integration;

/// <summary>
/// The 0011 backfill (ADR-038), tested the only way it can be: a database actually built by
/// <c>Migrate()</c> up to 0010, given pre-transition rows, then migrated forward. <c>EnsureCreated()</c>
/// skips the migration entirely, so it would pass against a schema that never had to be upgraded.
/// </summary>
/// <remarks>
/// The live database is upgraded by this path the next time the app or a job starts, so the row counts it
/// produces are the counts the Updates tab will show on day one. A backfill that silently put every show
/// into Renewed, or that dated an end from <c>mapped_at</c>, would look fine in the UI and be wrong
/// forever — hence asserting the exact instants here.
/// </remarks>
public sealed class StatusTransitionBackfillTests : IDisposable
{
    // The migration under test, named by id so the fixture can roll it back. EF Core 8's Migrate() has no
    // target-migration overload, so the "already-migrated, pre-transition" database the backfill exists for
    // has to be stood up by applying 0011 and then undoing it — which is exactly the state a real database
    // is in the moment before it starts.
    private const string TransitionMigration = "20261031170017_0011_ShowStatusTransitions";

    private const long EndedWithEpisodes = 7_700_001;
    private const long CanceledWithEpisodes = 7_700_002;
    private const long EndedNoDatedEpisodes = 7_700_003;
    private const long RunningWithEpisodes = 7_700_004;
    private const long EndedNoSourceUpdatedAt = 7_700_005;

    // TVmaze's own 'updated' for the shows that have one: 2026-01-09T12:00:00Z.
    private const long ProviderUpdated = 1_767_960_000;

    private readonly string _dbPath;

    public StatusTransitionBackfillTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"shows-adr038-backfill-{Guid.NewGuid():N}.db");

        // Seed while the model and the schema agree (the columns exist and are NULL, exactly as a real
        // pre-0011 row's would be), then roll 0011 back so the next Migrate() re-runs its Up(). Seeding
        // after the drop would fail instead: EF's model still has the columns and the INSERT would name
        // ones that no longer exist.
        using (var context = CreateContext())
        {
            context.Database.Migrate();
            SeedPreTransitionRows(context);
            RollBackTransitionMigration(context);
        }

        // The upgrade itself: this is the call the app's migrator makes, and it is what runs 0011.
        using (var context = CreateContext())
        {
            context.Database.Migrate();
        }
    }

    [Fact]
    public async Task A_terminal_show_is_dated_from_its_last_aired_episode()
    {
        // The rule the whole tab rests on: an end date is when the show stopped airing, not when we
        // noticed. The two dated episodes are deliberately in different years so a max() that went by
        // season order, or by mapped_at, lands somewhere else.
        var show = await ReadAsync(EndedWithEpisodes);

        Assert.Equal(ShowStatus.Ended, show.Status);
        Assert.Equal(new DateTime(2019, 6, 5, 21, 0, 0, DateTimeKind.Utc), show.StatusChangedAt!.Value.UtcDateTime);
    }

    [Fact]
    public async Task A_Canceled_show_is_backfilled_too()
    {
        // status_id IN (2, 3): terminal is the set {Ended, Canceled} from ShowStatusRules, and the
        // backfill has to agree with the mapper or a cancelled show reads as ended-today after the next
        // map.
        var show = await ReadAsync(CanceledWithEpisodes);

        Assert.Equal(ShowStatus.Canceled, show.Status);
        Assert.Equal(new DateTime(2021, 3, 14, 21, 0, 0, DateTimeKind.Utc), show.StatusChangedAt!.Value.UtcDateTime);
    }

    [Fact]
    public async Task A_terminal_show_with_no_dated_episode_is_left_null()
    {
        // No evidence, no date. The mapper writes a fallback on its next map rather than the backfill
        // inventing one here — which is why the column is nullable at all.
        Assert.Null((await ReadAsync(EndedNoDatedEpisodes)).StatusChangedAt);
    }

    [Fact]
    public async Task An_airing_show_is_never_dated()
    {
        var show = await ReadAsync(RunningWithEpisodes);

        Assert.Equal(ShowStatus.Running, show.Status);
        Assert.Null(show.StatusChangedAt);
    }

    [Fact]
    public async Task No_show_gets_a_renewal_date()
    {
        // The deliberate omission: nothing observed a transition before this migration, so backfilling
        // status_renewed_at would list every tracked show as renewed. Asserted across the whole table
        // rather than per row, because the mistake is a missing WHERE clause.
        await using var context = CreateContext();
        var stamped = await context.TrackedShows.CountAsync(s => s.StatusRenewedAt != null);

        Assert.Equal(0, stamped);
    }

    [Fact]
    public async Task Tvmaze_updated_at_is_copied_from_the_source_column()
    {
        // The mapped copy the Updated Lately section reads. strftime must land on the same 25-char UTC
        // shape the provider writes, or the section's window comparison is off.
        var show = await ReadAsync(EndedWithEpisodes);

        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(ProviderUpdated).UtcDateTime,
            show.TvmazeUpdatedAt!.Value.UtcDateTime);
    }

    [Fact]
    public async Task A_source_row_without_an_updated_value_leaves_it_null()
    {
        // Must not become the epoch, or every show TVmaze never reported 'updated' for would read as
        // updated in 1970.
        Assert.Null((await ReadAsync(EndedNoSourceUpdatedAt)).TvmazeUpdatedAt);
    }

    [Fact]
    public async Task Every_show_with_a_source_updated_value_is_backfilled()
    {
        // Row-count evidence for LOG.md: the backfill touching zero rows because of a join typo is the
        // failure mode a per-row assertion can miss if the seeded row happens to match.
        await using var context = CreateContext();
        var stamped = await context.TrackedShows.CountAsync(s => s.TvmazeUpdatedAt != null);

        Assert.Equal(4, stamped);
    }

    [Fact]
    public async Task A_second_Migrate_does_not_move_a_date_the_first_one_set()
    {
        // What actually happens a week later: the app starts again and Migrate() runs against an
        // already-upgraded database. 0011 is not re-applied, and nothing else may rewrite these columns —
        // a date that drifts on every start makes the 60-day window meaningless.
        await using (var context = CreateContext())
        {
            await context.Database.MigrateAsync();
        }

        var show = await ReadAsync(EndedWithEpisodes);
        Assert.Equal(new DateTime(2019, 6, 5, 21, 0, 0, DateTimeKind.Utc), show.StatusChangedAt!.Value.UtcDateTime);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(ProviderUpdated).UtcDateTime,
            show.TvmazeUpdatedAt!.Value.UtcDateTime);
    }

    // ---- helpers ----

    /// <summary>
    /// Puts the database back to how it looked before 0011: its row removed from the migration history and
    /// its three columns dropped, so the next <c>Migrate()</c> re-runs the <c>Up()</c> under test. The
    /// columns are what <c>Down()</c> drops, so this mirrors the migration's own reverse.
    /// </summary>
    private static void RollBackTransitionMigration(ShowsDbContext context)
    {
        // The column is snake_case because UseSnakeCaseNamingConvention applies to the history table too
        // (verified against the live DB: migration_id / product_version).
        context.Database.ExecuteSqlRaw(
            "DELETE FROM \"__EFMigrationsHistory\" WHERE migration_id = {0};", TransitionMigration);

        // Three literal statements rather than an interpolated column name: ExecuteSqlRaw splices the
        // string in unparameterised, so the identifier is written out rather than substituted.
        context.Database.ExecuteSqlRaw("ALTER TABLE tracked_show DROP COLUMN status_changed_at;");
        context.Database.ExecuteSqlRaw("ALTER TABLE tracked_show DROP COLUMN status_renewed_at;");
        context.Database.ExecuteSqlRaw("ALTER TABLE tracked_show DROP COLUMN tvmaze_updated_at;");
    }

    private static void SeedPreTransitionRows(ShowsDbContext context)
    {
        AddSource(context, EndedWithEpisodes, ProviderUpdated);
        AddSource(context, CanceledWithEpisodes, ProviderUpdated);
        AddSource(context, EndedNoDatedEpisodes, ProviderUpdated);
        AddSource(context, RunningWithEpisodes, ProviderUpdated);
        AddSource(context, EndedNoSourceUpdatedAt, providerUpdated: null);

        AddShow(context, EndedWithEpisodes, "Ended with episodes", ShowStatus.Ended);
        AddShow(context, CanceledWithEpisodes, "Canceled with episodes", ShowStatus.Canceled);
        AddShow(context, EndedNoDatedEpisodes, "Ended no dated episodes", ShowStatus.Ended);
        AddShow(context, RunningWithEpisodes, "Running with episodes", ShowStatus.Running);
        AddShow(context, EndedNoSourceUpdatedAt, "Ended no source updated", ShowStatus.Ended);

        var dated = new List<(long EpisodeId, string AirStamp)>();
        // Per-call ordinal for the episode id, and one season row per show: ix_season_show_num is unique on
        // (show_id, season_number), so a second episode goes on the same season rather than a twin row.
        var ordinal = 0;
        var seasonIds = new Dictionary<long, long>();
        // The finale is the newest dated episode on each of these; the earlier one exists so max() has to
        // actually choose.
        Air(EndedWithEpisodes, "2019-06-05 21:00:00+00:00");
        Air(EndedWithEpisodes, "2018-05-28 21:00:00+00:00");
        Air(CanceledWithEpisodes, "2021-03-14 21:00:00+00:00");
        Air(CanceledWithEpisodes, "2020-11-08 22:00:00+00:00");
        Air(RunningWithEpisodes, "2026-01-02 21:00:00+00:00");
        Air(EndedNoDatedEpisodes, airStamp: null);

        context.SaveChanges();

        // Applied after SaveChanges: the raw UPDATE below can only touch rows that are already in the
        // database, and the shape is pinned by hand because the backfill's max() is lexicographic — it is
        // only chronological because every stored air_stamp has this exact 25-char +00:00 form.
        foreach (var (episodeId, airStamp) in dated)
        {
            context.Database.ExecuteSqlRaw(
                "UPDATE episode SET air_stamp = {0} WHERE id = {1};",
                airStamp, episodeId.ToString(CultureInfo.InvariantCulture));
        }

        static void AddSource(ShowsDbContext ctx, long tvmazeId, long? providerUpdated) =>
            ctx.TvmazeShows.Add(new TvmazeShow
            {
                TvmazeId = (int) tvmazeId,
                Name = "Source",
                StatusRaw = "n/a",
                ProviderUpdated = providerUpdated,
                RawJson = "{}",
                FetchedAt = new DateTimeOffset(2026, 1, 9, 12, 0, 0, TimeSpan.Zero),
            });

        static void AddShow(ShowsDbContext ctx, long id, string title, ShowStatus status)
        {
            var show = new TrackedShow
            {
                Id = id,
                TvmazeId = (int) id,
                Title = title,
                Status = status,
                // mapped_at is the column that moves on any field edit — the one the backfill must NOT use.
                MappedAt = new DateTimeOffset(2026, 10, 1, 8, 30, 0, TimeSpan.Zero),
            };
            ctx.Entry(show).Property("tvmaze_show_id").CurrentValue = (int) id;
            ctx.TrackedShows.Add(show);
        }

        void Air(long showId, string? airStamp)
        {
            if (!seasonIds.TryGetValue(showId, out var seasonId))
            {
                seasonId = showId * 10;
                context.Seasons.Add(new Season
                {
                    Id = seasonId,
                    ShowId = showId,
                    SeasonNumber = 1,
                });
                seasonIds[showId] = seasonId;
            }

            var episode = new Episode
            {
                Id = showId * 100 + ordinal,
                ShowId = showId,
                SeasonId = seasonId,
                SeasonNumber = 1,
                // ix_episode_key is unique on (show_id, season_number, episode_number), so the second
                // episode on a show has to differ in number as well.
                EpisodeNumber = ++ordinal,
                Title = "Episode",
                EpisodeType = EpisodeType.Regular,
            };
            context.Episodes.Add(episode);

            if (airStamp is not null)
            {
                dated.Add((episode.Id, airStamp));
            }
        }
    }

    private async Task<TrackedShow> ReadAsync(long id)
    {
        await using var context = CreateContext();
        return await context.TrackedShows.AsNoTracking().SingleAsync(s => s.Id == id);
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
}
