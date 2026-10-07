using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shows.Infrastructure.Configuration;
using Shows.Infrastructure.Import;
using Shows.Infrastructure.Persistence;
using Shows.Infrastructure.Persistence.Staging;
using Xunit;

namespace Shows.Tests.Integration;

/// <summary>
/// Step 2.4 acceptance: <c>--report</c> exports <c>v_staging_issues</c> to a CSV and the printed counts
/// match the view grouped by severity. Seeds a known blocking row, a known warning row, and a clean row,
/// then asserts the file has the header plus exactly the two issue rows (blocking first) and the counts
/// agree — the same cross-check the plan states against
/// <c>SELECT severity, COUNT(*) FROM v_staging_issues GROUP BY severity</c>.
/// </summary>
public sealed class StagingReportExporterTests : IDisposable
{
    private readonly string _dbPath;
    private readonly string _reportPath;

    public StagingReportExporterTests()
    {
        var root = Path.Combine(Path.GetTempPath(), $"shows-2-4-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        _dbPath = Path.Combine(root, "shows.db");
        _reportPath = Path.Combine(root, "staging_issues.csv");

        using var context = CreateContext();
        context.Database.Migrate();

        // One blocking (tvmaze_id null), one warning (zero total), one clean (no issue).
        context.StagingShows.AddRange(
            Show(100, "Blocking, Show", tvmazeId: null, imdbId: "tt1", status: "watching", total: 5, watched: 1),
            Show(200, "Warning Show", tvmazeId: 100, imdbId: "tt2", status: "later", total: 0, watched: 0),
            Show(300, "Clean Show", tvmazeId: 100, imdbId: "tt3", status: "later", total: 5, watched: 1));
        context.SaveChanges();
    }

    [Fact]
    public async Task Export_writes_blocking_then_warning_rows_and_counts_match_the_view()
    {
        using var context = CreateContext();
        var exporter = new StagingReportExporter(
            context, CreatePaths(), NullLogger<StagingReportExporter>.Instance);

        var result = await exporter.ExportAsync(CancellationToken.None);

        Assert.Equal(_reportPath, result.Path);
        Assert.Equal(2, result.Total);
        Assert.Equal(1, result.Blocking);
        Assert.Equal(1, result.Warning);

        // The counts must equal the view grouped by severity — the plan's cross-check.
        var bySeverity = await context.StagingIssues
            .GroupBy(i => i.Severity)
            .Select(g => new { Severity = g.Key, Count = g.Count() })
            .ToListAsync();
        Assert.Equal(1, bySeverity.Single(x => x.Severity == "blocking").Count);
        Assert.Equal(1, bySeverity.Single(x => x.Severity == "warning").Count);

        var lines = await File.ReadAllLinesAsync(_reportPath, Encoding.UTF8);
        Assert.Equal("myshows_id,title_ru,issue,severity", lines[0]);
        Assert.Equal(3, lines.Length); // header + 2 rows
        // Blocking (id 100) first; its title contains a comma so it is quoted (RFC 4180).
        Assert.Equal("100,\"Blocking, Show\",unresolved_tvmaze_id,blocking", lines[1]);
        Assert.Equal("200,Warning Show,zero_total_episodes,warning", lines[2]);
    }

    private static StagingShow Show(
        int id, string title, int? tvmazeId, string? imdbId, string status, int total, int watched) => new()
    {
        MyshowsId = id,
        TitleRu = title,
        SourceFile = "watching.json",
        SourceWatchStatus = status,
        TotalEpisodes = total,
        WatchedEpisodes = watched,
        ImdbId = imdbId,
        TvmazeId = tvmazeId,
        TvmazeResolution = "scraped",
        NeedsReview = false,
    };

    private DataPaths CreatePaths()
    {
        var options = Options.Create(new ShowsOptions
        {
            Data =
            {
                BaseDirectory = Path.GetDirectoryName(_dbPath)!,
                DatabasePath = Path.GetFileName(_dbPath),
                ReportPath = Path.GetFileName(_reportPath),
            },
        });
        return new DataPaths(options);
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
        var root = Path.GetDirectoryName(_dbPath)!;
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
