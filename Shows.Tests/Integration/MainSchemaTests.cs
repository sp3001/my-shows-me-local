using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Shows.Domain;
using Shows.Infrastructure;
using Shows.Infrastructure.Persistence;
using Shows.Infrastructure.Persistence.Sources;
using Xunit;

namespace Shows.Tests.Integration;

/// <summary>
/// Step 1.2 acceptance: the mapped and user tables behave as SCHEMA.md §2 specifies. The two
/// properties that are easy to get wrong and invisible when wrong — the partial unique index on
/// <c>imdb_id</c> (a plain unique constraint would reject the second NULL) and the absence of user
/// columns on <c>tracked_show</c> (a leftover column silently makes it un-rebuildable) — are asserted
/// directly against the shipped DDL and data, not against the model.
/// </summary>
/// <remarks>
/// Every <c>tracked_show</c> row needs a parent <c>tvmaze_show</c> (the NOT NULL FK from step 1.2b),
/// so the seeding helper creates both. The shadow FK column is set through the entry API because
/// Domain carries no source-table reference (SPEC P3).
/// </remarks>
public sealed class MainSchemaTests : IClassFixture<DatabaseFixture>, IDisposable
{
    private readonly DatabaseFixture _fixture;

    public MainSchemaTests(DatabaseFixture fixture) => _fixture = fixture;

    private ShowsDbContext CreateContext() => _fixture.CreateContext();

    [Fact]
    public void Tracked_show_ddl_has_no_user_columns()
    {
        // SCHEMA.md §2 / ADR-024: category_id, added_at, needs_review live on user_show. A leftover
        // column here makes tracked_show un-rebuildable, and the failure is invisible until a restore.
        var ddl = QuerySqliteMaster("tracked_show");

        Assert.DoesNotContain("category_id", ddl, StringComparison.Ordinal);
        Assert.DoesNotContain("added_at", ddl, StringComparison.Ordinal);
        Assert.DoesNotContain("needs_review", ddl, StringComparison.Ordinal);
        Assert.DoesNotContain("category_changed_at", ddl, StringComparison.Ordinal);
    }

    [Fact]
    public void Tracked_show_tvmaze_id_is_unique()
    {
        // Two tracked_show rows claiming the same tvmaze_id must collide, even with distinct source rows.
        using var context = CreateContext();
        SeedShow(context, id: 1000, tvmazeId: 77, sourceId: 77);
        context.SaveChanges();

        SeedShow(context, id: 1001, tvmazeId: 77, sourceId: 7777);
        Assert.Throws<DbUpdateException>(() => context.SaveChanges());
    }

    [Fact]
    public void Two_null_imdb_ids_are_both_allowed_by_the_partial_index()
    {
        // The partial unique index (WHERE imdb_id IS NOT NULL) — a plain unique constraint would fail
        // this, because SQLite treats NULLs as distinct only in the filtered case.
        using var context = CreateContext();
        SeedShow(context, id: 2000, tvmazeId: 200, sourceId: 200, imdbId: null);
        SeedShow(context, id: 2001, tvmazeId: 201, sourceId: 201, imdbId: null);
        context.SaveChanges(); // must not throw

        Assert.Equal(2, context.TrackedShows.Count(s => s.ImdbId == null));
    }

    [Fact]
    public void Duplicate_non_null_imdb_id_is_rejected()
    {
        using var context = CreateContext();
        SeedShow(context, id: 3000, tvmazeId: 300, sourceId: 300, imdbId: "tt111");
        context.SaveChanges();

        SeedShow(context, id: 3001, tvmazeId: 301, sourceId: 301, imdbId: "tt111");
        Assert.Throws<DbUpdateException>(() => context.SaveChanges());
    }

    [Fact]
    public void Episode_with_only_one_provider_id_inserts()
    {
        // ADR-025: both provider ids are nullable and symmetric. A TVmaze-only and an IMDb-only episode
        // must both be storable.
        using var context = CreateContext();
        var show = SeedShow(context, id: 4000, tvmazeId: 400, sourceId: 400);
        var season = new Season { Id = 4001, ShowId = show.Id, SeasonNumber = 1 };
        show.Seasons.Add(season);
        season.Episodes.Add(new Episode { Id = 4002, ShowId = show.Id, SeasonId = season.Id, SeasonNumber = 1, EpisodeNumber = 1, TvmazeEpisodeId = 9001, ImdbId = null });
        season.Episodes.Add(new Episode { Id = 4003, ShowId = show.Id, SeasonId = season.Id, SeasonNumber = 1, EpisodeNumber = 2, TvmazeEpisodeId = null, ImdbId = "tt500" });
        context.SaveChanges();

        Assert.NotNull(context.Episodes.Find(4002L));
        Assert.NotNull(context.Episodes.Find(4003L));
    }

    [Fact]
    public void User_show_shares_its_primary_key_with_tracked_show()
    {
        using var context = CreateContext();
        var show = SeedShow(context, id: 5000, tvmazeId: 500, sourceId: 500);
        show.User = new UserShow
        {
            TrackedShowId = show.Id,
            Category = ShowCategory.Started,
            AddedAt = DateTimeOffset.UtcNow,
            CategoryChangedAt = DateTimeOffset.UtcNow,
        };
        context.SaveChanges();

        // The user_show PK equals the tracked_show PK — no separate surrogate key.
        var user = context.UserShows.Find(show.Id);
        Assert.NotNull(user);
        Assert.Equal(show.Id, user!.TrackedShowId);
    }

    [Fact]
    public void Tracked_show_requires_a_source_row()
    {
        // SCHEMA.md §2: tvmaze_show_id is NOT NULL. A mapped row cannot exist without its source row —
        // that is the rebuildability property (SPEC P4). Insert without a parent must fail.
        using var context = CreateContext();
        var orphan = new TrackedShow { Id = 6000, TvmazeId = 600, Title = "Orphan" };
        context.TrackedShows.Add(orphan);
        Assert.Throws<DbUpdateException>(() => context.SaveChanges());
    }

    [Fact]
    public void Every_primary_key_column_is_integer()
    {
        // SCHEMA.md §1: surrogate PKs are INTEGER (long). Assert against the shipped DDL.
        using var connection = _fixture.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT m.name, p.name, p.type FROM sqlite_master m "
            + "JOIN pragma_table_info(m.name) p ON 1=1 "
            + "WHERE m.type='table' AND p.pk > 0 AND m.name NOT LIKE '__EF%';";
        using var reader = command.ExecuteReader();

        var checkedColumns = 0;
        while (reader.Read())
        {
            var table = reader.GetString(0);
            var column = reader.GetString(1);
            var type = reader.GetString(2);

            // Natural TEXT keys by design (SCHEMA.md §2–§4); every surrogate id must be INTEGER.
            var isNaturalTextKey = (table == "writer_lease" && column == "name")
                || (table == "http_cache" && column == "url")
                || (table == "tvmaze_status_seen" && column == "status_raw")
                || (table == "tvmaze_akas" && column == "country_code")
                || (table == "tvmaze_akas" && column == "name")
                || (table == "imdb_title" && column == "tconst")
                || (table == "imdb_episode" && column == "tconst")
                || (table == "imdb_akas" && column == "title_id")
                || (table == "imdb_dataset_meta" && column == "dataset_name")
                || (table == "discovery_schedule_archive" && column == "fetched_on");

            if (!isNaturalTextKey)
            {
                Assert.Equal("INTEGER", type);
            }

            checkedColumns++;
        }

        Assert.True(checkedColumns >= 8, $"Expected at least 8 PK columns, found {checkedColumns}.");
    }

    [Fact]
    public void Shipped_ddl_contains_no_autoincrement()
    {
        using var connection = _fixture.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT sql FROM sqlite_master WHERE type='table';";
        using var reader = command.ExecuteReader();

        while (reader.Read())
        {
            var sql = reader.GetString(0);
            Assert.DoesNotContain("AUTOINCREMENT", sql, StringComparison.OrdinalIgnoreCase);
        }
    }

    private string QuerySqliteMaster(string tableName)
    {
        using var connection = _fixture.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT sql FROM sqlite_master WHERE name = $name;";
        command.Parameters.AddWithValue("$name", tableName);
        return (string)command.ExecuteScalar()!;
    }

    // Creates the source row and the mapped row together, and sets the shadow FK. The mapper is the
    // only production writer of both; tests stand in for it here.
    private static TrackedShow SeedShow(
        ShowsDbContext context, long id, int tvmazeId, int sourceId, string? imdbId = null)
    {
        context.TvmazeShows.Add(new TvmazeShow
        {
            TvmazeId = sourceId,
            Name = "Test",
            StatusRaw = "Running",
            RawJson = "{}",
            FetchedAt = DateTimeOffset.UtcNow,
        });

        var show = new TrackedShow { Id = id, TvmazeId = tvmazeId, Title = "Test", ImdbId = imdbId };
        context.Entry(show).Property("tvmaze_show_id").CurrentValue = sourceId;
        context.TrackedShows.Add(show);
        return show;
    }

    public void Dispose() => _fixture.Reset();
}

/// <summary>One database per test class, created once, reset between tests.</summary>
public sealed class DatabaseFixture : IDisposable
{
    private readonly string _dbPath;

    public DatabaseFixture()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"shows-1-2-{Guid.NewGuid():N}.db");
        using var context = CreateContext();
        context.Database.EnsureCreated();
    }

    public ShowsDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<ShowsDbContext>()
            .UseSqlite(new SqliteConnectionStringBuilder { DataSource = _dbPath }.ToString())
            .UseSnakeCaseNamingConvention()
            .AddInterceptors(new SqlitePragmaInterceptor())
            .Options;
        return new ShowsDbContext(options);
    }

    // Mirrors the PRODUCTION context configuration exactly by calling the one method production uses
    // (ConfigureShowsSqlite). CreateContext() above hand-builds options with the provider default, which
    // is fine for schema tests but cannot catch a regression in how production configures the provider —
    // e.g. reintroducing a global SplitQuery, which crashes the mapper's explicit collection loads. Any
    // test that must exercise production query behavior uses this instead.
    public ShowsDbContext CreateProductionContext()
    {
        var options = ShowsInfrastructureServiceCollectionExtensions.ConfigureShowsSqlite(
                new DbContextOptionsBuilder<ShowsDbContext>(),
                new SqliteConnectionStringBuilder { DataSource = _dbPath }.ToString())
            .Options;
        return new ShowsDbContext(options);
    }

    public SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _dbPath }.ToString());
        connection.Open();
        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA foreign_keys=ON;";
        pragma.ExecuteNonQuery();
        return connection;
    }

    public void Reset()
    {
        using var context = CreateContext();
        context.Database.ExecuteSqlRaw(
            "DELETE FROM episode_watch; DELETE FROM ui_season_state; DELETE FROM episode; "
            + "DELETE FROM show_genre; DELETE FROM season; DELETE FROM user_show; "
            + "DELETE FROM tracked_show; DELETE FROM genre; DELETE FROM tvmaze_episode; "
            + "DELETE FROM tvmaze_akas; DELETE FROM tvmaze_show; DELETE FROM tvmaze_status_seen; "
            + "DELETE FROM imdb_episode; DELETE FROM imdb_title; DELETE FROM imdb_akas; "
            + "DELETE FROM imdb_dataset_meta;");
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
