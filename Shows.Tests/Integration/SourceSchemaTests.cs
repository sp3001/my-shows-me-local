using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Shows.Infrastructure.Persistence;
using Shows.Infrastructure.Persistence.Sources;
using Xunit;

namespace Shows.Tests.Integration;

/// <summary>
/// Step 1.2b acceptance: the source tables hold provider payloads verbatim (SCHEMA.md §3–§4). The two
/// properties that matter — <c>tvmaze_show</c> must accept <c>runtime = NULL</c> with
/// <c>average_runtime = 62</c> (the Black Mirror case, DATA_ANALYSIS.md §8), and
/// <c>tvmaze_status_seen.status_raw</c> must be a PK so a repeated string cannot double-count — are
/// asserted against the shipped DDL and data.
/// </summary>
public sealed class SourceSchemaTests : IClassFixture<DatabaseFixture>, IDisposable
{
    private readonly DatabaseFixture _fixture;

    public SourceSchemaTests(DatabaseFixture fixture) => _fixture = fixture;

    private ShowsDbContext CreateContext() => _fixture.CreateContext();

    [Fact]
    public void Tvmaze_show_accepts_null_runtime_with_average_runtime()
    {
        // DATA_ANALYSIS.md §8: Black Mirror has runtime=null, averageRuntime=62. Collapsing them at
        // ingest would lose the distinction; the source table must hold both independently.
        using var context = CreateContext();
        context.TvmazeShows.Add(new TvmazeShow
        {
            TvmazeId = 305,
            Name = "Black Mirror",
            StatusRaw = "Running",
            RuntimeMinutes = null,
            AverageRuntime = 62,
            RawJson = "{}",
            FetchedAt = DateTimeOffset.UtcNow,
        });
        context.SaveChanges();

        var stored = context.TvmazeShows.Find(305);
        Assert.NotNull(stored);
        Assert.Null(stored!.RuntimeMinutes);
        Assert.Equal(62, stored.AverageRuntime);
    }

    [Fact]
    public void Tvmaze_status_seen_pk_prevents_double_counting()
    {
        // SCHEMA.md §3: status_raw is the PK, so a repeated string cannot create a second row — the
        // counter is incremented instead. A second insert with the same status_raw must fail at the
        // database (a fresh context, so EF's change tracker is not what rejects it).
        using (var context = CreateContext())
        {
            context.TvmazeStatusSeen.Add(new TvmazeStatusSeen
            {
                StatusRaw = "Running",
                FirstSeenAt = DateTimeOffset.UtcNow,
                LastSeenAt = DateTimeOffset.UtcNow,
                Occurrences = 1,
                MappedTo = 1,
            });
            context.SaveChanges();
        }

        using (var context = CreateContext())
        {
            context.TvmazeStatusSeen.Add(new TvmazeStatusSeen
            {
                StatusRaw = "Running",
                FirstSeenAt = DateTimeOffset.UtcNow,
                LastSeenAt = DateTimeOffset.UtcNow,
                Occurrences = 1,
                MappedTo = 1,
            });
            Assert.Throws<DbUpdateException>(() => context.SaveChanges());
        }
    }

    [Fact]
    public void Source_columns_use_provider_field_names_verbatim()
    {
        // SCHEMA.md §3–§4: column names mirror the API/TSV keys. Renaming them to local conventions is
        // where mapping bugs hide. Assert against the shipped DDL.
        var tvmazeShowDdl = QuerySqliteMaster("tvmaze_show");
        Assert.Contains("status_raw", tvmazeShowDdl, StringComparison.Ordinal);
        Assert.Contains("average_runtime", tvmazeShowDdl, StringComparison.Ordinal);
        Assert.Contains("raw_json", tvmazeShowDdl, StringComparison.Ordinal);
        Assert.Contains("summary_html", tvmazeShowDdl, StringComparison.Ordinal);
        Assert.Contains("provider_updated", tvmazeShowDdl, StringComparison.Ordinal);

        var tvmazeEpisodeDdl = QuerySqliteMaster("tvmaze_episode");
        Assert.Contains("airstamp", tvmazeEpisodeDdl, StringComparison.Ordinal);
        Assert.Contains("airdate", tvmazeEpisodeDdl, StringComparison.Ordinal);
        Assert.Contains("type_raw", tvmazeEpisodeDdl, StringComparison.Ordinal);

        var imdbEpisodeDdl = QuerySqliteMaster("imdb_episode");
        Assert.Contains("parent_tconst", imdbEpisodeDdl, StringComparison.Ordinal);
    }

    [Fact]
    public void Imdb_episode_season_number_is_nullable_not_zero()
    {
        // SCHEMA.md §4: \N maps to NULL, never 0. A 0 would collide with a real season 0 (specials).
        using var context = CreateContext();
        context.ImdbTitles.Add(new ImdbTitle
        {
            Tconst = "tt100",
            TitleType = "tvSeries",
            PrimaryTitle = "Test",
            IsAdult = false,
        });
        context.ImdbEpisodes.Add(new ImdbEpisode
        {
            Tconst = "tt101",
            ParentTconst = "tt100",
            SeasonNumber = null,
            EpisodeNumber = null,
            PrimaryTitle = "Legacy",
        });
        context.SaveChanges();

        var episode = context.ImdbEpisodes.Find("tt101");
        Assert.NotNull(episode);
        Assert.Null(episode!.SeasonNumber);
        Assert.Null(episode.EpisodeNumber);
    }

    [Fact]
    public void Tracked_show_fk_to_tvmaze_show_is_not_null()
    {
        // SCHEMA.md §2: tvmaze_show_id is NOT NULL. Assert against the shipped DDL.
        var ddl = QuerySqliteMaster("tracked_show");
        Assert.Contains("tvmaze_show_id", ddl, StringComparison.Ordinal);
        Assert.Contains("NOT NULL", ddl, StringComparison.Ordinal);
    }

    private string QuerySqliteMaster(string tableName)
    {
        using var connection = _fixture.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT sql FROM sqlite_master WHERE name = $name;";
        command.Parameters.AddWithValue("$name", tableName);
        return (string)command.ExecuteScalar()!;
    }

    public void Dispose() => _fixture.Reset();
}
