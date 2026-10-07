using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Shows.Infrastructure.Persistence;
using Shows.Infrastructure.Persistence.Staging;
using Xunit;

namespace Shows.Tests.Integration;

/// <summary>
/// Step 1.3 acceptance: <c>v_staging_issues</c> is the promote gate, so every branch of its CASE must
/// produce the exact <c>issue</c> and <c>severity</c> SCHEMA.md §6 specifies — an untested branch means
/// either a blocked import or a silently-wrong promotion. The view is created by hand-written SQL in
/// migration 0003, so this fixture runs <c>Migrate()</c> (not <c>EnsureCreated</c>, which skips views).
/// </summary>
public sealed class StagingViewTests : IClassFixture<StagingViewTests.MigratedFixture>
{
    private readonly MigratedFixture _fixture;

    public StagingViewTests(MigratedFixture fixture) => _fixture = fixture;

    [Fact]
    public void Null_tvmaze_id_is_blocking_unresolved_tvmaze_id()
    {
        AssertIssue(
            Insert("tvmaze_id IS NULL", tvmazeId: null, imdbId: "tt1", status: "watching", total: 5, watched: 1),
            issue: "unresolved_tvmaze_id", severity: "blocking");
    }

    [Fact]
    public void Null_imdb_id_is_warning_unresolved_imdb_id()
    {
        // ADR-030: a tvmaze-resolved show is never BLOCKING for lacking an imdb_id — IMDb is
        // enrichment, not a promotion prerequisite. The row is still surfaced, as a warning.
        AssertIssue(
            Insert("imdb_id IS NULL", tvmazeId: 100, imdbId: null, status: "watching", total: 5, watched: 1),
            issue: "unresolved_imdb_id", severity: "warning");
    }

    [Fact]
    public void Watching_with_watched_exceeding_total_is_blocking()
    {
        // The one blocking arithmetic case (SCHEMA.md §6).
        AssertIssue(
            Insert("watched>total", tvmazeId: 100, imdbId: "tt1", status: "watching", total: 5, watched: 6),
            issue: "watched_exceeds_total", severity: "blocking");
    }

    [Fact]
    public void Zero_total_episodes_is_warning()
    {
        AssertIssue(
            Insert("zero total", tvmazeId: 100, imdbId: "tt1", status: "later", total: 0, watched: 0),
            issue: "zero_total_episodes", severity: "warning");
    }

    [Fact]
    public void Imdb_count_mismatch_is_warning()
    {
        // total=5 but only 3 imdb_episode rows → imdb_count_mismatch.
        var id = Insert("imdb mismatch", tvmazeId: 100, imdbId: "ttIMDB", status: "later", total: 5, watched: 1);
        SeedImdbEpisodes("ttIMDB", count: 3);
        AssertIssue(id, issue: "imdb_count_mismatch", severity: "warning");
    }

    [Fact]
    public void Tvmaze_count_mismatch_is_warning()
    {
        // imdb count matches (5), tvmaze count does not (3) → tvmaze_count_mismatch.
        var id = Insert("tvm mismatch", tvmazeId: 777, imdbId: "ttTV", status: "later", total: 5, watched: 1);
        SeedImdbEpisodes("ttTV", count: 5);
        SeedTvmazeShow(777);
        SeedTvmazeEpisodes(777, count: 3);
        AssertIssue(id, issue: "tvmaze_count_mismatch", severity: "warning");
    }

    [Fact]
    public void Fully_resolved_show_produces_no_issue()
    {
        // All counts match, nothing null, watched <= total → the row must not appear in the view.
        var id = Insert("clean", tvmazeId: 888, imdbId: "ttOK", status: "later", total: 5, watched: 1);
        SeedImdbEpisodes("ttOK", count: 5);
        SeedTvmazeShow(888);
        SeedTvmazeEpisodes(888, count: 5);

        using var context = _fixture.CreateContext();
        Assert.DoesNotContain(id, context.StagingIssues.Select(i => i.MyshowsId).ToList());
    }

    private int Insert(
        string label, int? tvmazeId, string? imdbId, string status, int total, int watched)
    {
        var id = Math.Abs(label.GetHashCode()) % 100000 + 1000;
        using var context = _fixture.CreateContext();
        context.Database.ExecuteSqlRaw(
            "INSERT INTO staging_show (myshows_id, title_ru, source_file, source_watch_status, "
            + "total_episodes, watched_episodes, imdb_id, tvmaze_id, tvmaze_resolution, needs_review) "
            + $"VALUES ({id}, '{label}', 'watching.json', '{status}', {total}, {watched}, "
            + $"{(imdbId is null ? "NULL" : $"'{imdbId}'")}, "
            + $"{(tvmazeId is null ? "NULL" : tvmazeId.ToString())}, 'scraped', 0);");
        return id;
    }

    private void SeedImdbEpisodes(string parentTconst, int count)
    {
        using var context = _fixture.CreateContext();
        context.Database.ExecuteSqlRaw(
            $"INSERT INTO imdb_title (tconst, title_type, primary_title, is_adult) "
            + $"VALUES ('{parentTconst}', 'tvSeries', 'X', 0);");
        for (var i = 1; i <= count; i++)
        {
            context.Database.ExecuteSqlRaw(
                "INSERT INTO imdb_episode (tconst, parent_tconst, season_number, episode_number, primary_title) "
                + $"VALUES ('{parentTconst}e{i}', '{parentTconst}', 1, {i}, 'E');");
        }
    }

    private void SeedTvmazeShow(int tvmazeId)
    {
        using var context = _fixture.CreateContext();
        context.Database.ExecuteSqlRaw(
            "INSERT INTO tvmaze_show (tvmaze_id, status_raw, raw_json, fetched_at) "
            + $"VALUES ({tvmazeId}, 'Running', 'json', '2026-01-01T00:00:00+00:00');");
    }

    private void SeedTvmazeEpisodes(int tvmazeShowId, int count)
    {
        using var context = _fixture.CreateContext();
        for (var i = 1; i <= count; i++)
        {
            context.Database.ExecuteSqlRaw(
                "INSERT INTO tvmaze_episode (tvmaze_episode_id, tvmaze_show_id, season_number, type_raw, raw_json, fetched_at) "
                + $"VALUES ({tvmazeShowId * 1000 + i}, {tvmazeShowId}, 1, 'regular', 'json', '2026-01-01T00:00:00+00:00');");
        }
    }

    private void AssertIssue(int myshowsId, string issue, string severity)
    {
        using var context = _fixture.CreateContext();
        var row = context.StagingIssues.AsNoTracking().Single(i => i.MyshowsId == myshowsId);
        Assert.Equal(issue, row.Issue);
        Assert.Equal(severity, row.Severity);
    }

    /// <summary>A database built by Migrate() so the hand-written view exists.</summary>
    public sealed class MigratedFixture : IDisposable
    {
        private readonly string _dbPath;

        public MigratedFixture()
        {
            _dbPath = Path.Combine(Path.GetTempPath(), $"shows-1-3-{Guid.NewGuid():N}.db");
            using var context = CreateContext();
            context.Database.Migrate();
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

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(_dbPath))
            {
                File.Delete(_dbPath);
            }
        }
    }
}
