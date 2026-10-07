using System.IO.Compression;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shows.Application;
using Shows.Domain;
using Shows.Infrastructure.Configuration;
using Shows.Infrastructure.Import;
using Shows.Infrastructure.Persistence;
using Shows.Infrastructure.Persistence.Sources;
using Xunit;

namespace Shows.Tests.Integration;

/// <summary>
/// Step 7.4 acceptance for the <c>ImdbImport</c> job, driven by fixture TSVs written to a temp
/// <c>data/imdb</c> so no network is touched. The assertions are the ones the plan names verbatim:
/// <c>tt26448526</c> yields 33 episode rows, <c>tt2085059</c> yields 32, a second run reports
/// <c>items_changed = 0</c>, the dataset files are kept on disk, and a soft-deleted show's imdb_id is
/// never loaded (ADR-021 / R8).
/// </summary>
public sealed class ImdbImporterTests : IDisposable
{
    // The two tri-source-validated shows (DATA_ANALYSIS §6): 33 and 32 episode rows respectively.
    private const string BrilliantMinds = "tt26448526"; // 33 episodes
    private const string BlackMirror = "tt2085059";     // 32 episodes
    private const string Untracked = "tt99999999";      // never loaded

    private readonly string _dbPath;
    private readonly string _baseDir;
    private readonly string _imdbDir;
    private readonly SequentialIdGenerator _ids = new();

    public ImdbImporterTests()
    {
        _baseDir = Path.Combine(Path.GetTempPath(), $"shows-7-4-{Guid.NewGuid():N}");
        _dbPath = Path.Combine(_baseDir, "shows.db");
        _imdbDir = Path.Combine(_baseDir, "data", "imdb");
        Directory.CreateDirectory(_imdbDir);
        using var context = CreateContext();
        context.Database.EnsureCreated();
    }

    [Fact]
    public async Task Loads_only_tracked_shows_with_the_documented_episode_counts()
    {
        SeedTracked(BrilliantMinds, tvmazeId: 1);
        SeedTracked(BlackMirror, tvmazeId: 2);
        WriteFixtures();

        var report = await RunImporter();

        Assert.False(report.Refused);

        await using var db = CreateContext();
        Assert.Equal(33, await db.ImdbEpisodes.CountAsync(e => e.ParentTconst == BrilliantMinds));
        Assert.Equal(32, await db.ImdbEpisodes.CountAsync(e => e.ParentTconst == BlackMirror));

        // ADR-021: the untracked parent's rows are never loaded.
        Assert.Equal(0, await db.ImdbEpisodes.CountAsync(e => e.ParentTconst == Untracked));
        Assert.False(await db.ImdbTitles.AnyAsync(t => t.Tconst == Untracked));

        // The pass-3 UPDATE filled episode detail from title.basics (tvEpisode rows).
        var first = await db.ImdbEpisodes.FirstAsync(e => e.ParentTconst == BrilliantMinds && e.SeasonNumber == 1 && e.EpisodeNumber == 1);
        Assert.Equal("Episode 1", first.PrimaryTitle);
        Assert.Equal(2024, first.StartYear);
    }

    [Fact]
    public async Task Null_markers_become_null_never_zero()
    {
        SeedTracked(BrilliantMinds, tvmazeId: 1);
        WriteFixtures(); // one episode has \N season/episode

        await RunImporter();

        await using var db = CreateContext();
        var legacy = await db.ImdbEpisodes.FirstAsync(e => e.Tconst == $"{BrilliantMinds}_legacy");
        Assert.Null(legacy.SeasonNumber);
        Assert.Null(legacy.EpisodeNumber);
    }

    [Fact]
    public async Task Second_run_reports_zero_changes()
    {
        SeedTracked(BrilliantMinds, tvmazeId: 1);
        SeedTracked(BlackMirror, tvmazeId: 2);
        WriteFixtures();

        await RunImporter();
        var second = await RunImporter();

        // INSERT OR IGNORE + a value-guarded UPDATE: the same files change nothing on a re-run.
        Assert.Equal(0, second.Changed);
    }

    [Fact]
    public async Task Dataset_files_are_kept_on_disk_after_the_run()
    {
        SeedTracked(BrilliantMinds, tvmazeId: 1);
        // Empty imdb dir: the downloader must fetch (here: write a .tsv.gz) and the file must survive.
        var downloader = new FakeDownloader()
            .With("title.episode", EpisodeFixture([BrilliantMinds], counts: new() { [BrilliantMinds] = 33 }))
            .With("title.basics", BasicsFixture([BrilliantMinds], counts: new() { [BrilliantMinds] = 33 }))
            .With("title.akas", AkasFixture([BrilliantMinds]));

        await RunImporter(downloader);

        // Kept, not deleted — a schema change is a re-load, not a re-download (step 7.4).
        Assert.True(File.Exists(Path.Combine(_imdbDir, "title.episode.tsv.gz")));
        Assert.True(File.Exists(Path.Combine(_imdbDir, "title.basics.tsv.gz")));
        Assert.Equal(33, await CountEpisodes(BrilliantMinds));
    }

    [Fact]
    public async Task A_show_already_on_disk_is_reused_without_a_download()
    {
        SeedTracked(BrilliantMinds, tvmazeId: 1);
        WriteFixtures(); // plain .tsv already present
        var downloader = new FakeDownloader(); // would throw if called

        await RunImporter(downloader);

        Assert.False(downloader.Called); // reuse, not re-download
        Assert.Equal(33, await CountEpisodes(BrilliantMinds));
    }

    [Fact]
    public async Task Soft_deleted_shows_imdb_id_is_never_loaded()
    {
        SeedTracked(BrilliantMinds, tvmazeId: 1);
        SeedTracked(BlackMirror, tvmazeId: 2, deleted: true); // R8: deleted_at set
        WriteFixtures();

        await RunImporter();

        await using var db = CreateContext();
        Assert.Equal(33, await db.ImdbEpisodes.CountAsync(e => e.ParentTconst == BrilliantMinds));
        Assert.Equal(0, await db.ImdbEpisodes.CountAsync(e => e.ParentTconst == BlackMirror));
    }

    [Fact]
    public async Task Akas_are_loaded_for_tracked_shows_only()
    {
        SeedTracked(BrilliantMinds, tvmazeId: 1);
        WriteFixtures();

        await RunImporter();

        await using var db = CreateContext();
        Assert.Equal(2, await db.ImdbAkas.CountAsync(a => a.TitleId == BrilliantMinds));
        Assert.Equal(0, await db.ImdbAkas.CountAsync(a => a.TitleId == Untracked));
    }

    [Fact]
    public async Task Refuses_when_another_writer_holds_the_lease()
    {
        SeedTracked(BrilliantMinds, tvmazeId: 1);
        WriteFixtures();

        var report = await RunImporter(grantLease: false);

        Assert.True(report.Refused);
        Assert.Equal(0, report.Changed);
    }

    [Fact]
    public async Task Records_dataset_meta_for_each_loaded_dataset()
    {
        SeedTracked(BrilliantMinds, tvmazeId: 1);
        WriteFixtures();

        await RunImporter();

        await using var db = CreateContext();
        var names = await db.ImdbDatasetMeta.Select(m => m.DatasetName).ToListAsync();
        Assert.Contains("title.episode", names);
        Assert.Contains("title.basics", names);
        Assert.Contains("title.akas", names);
    }

    // ---- fixtures ----

    // Write the three datasets as plain .tsv into the imdb dir (the reuse path).
    private void WriteFixtures()
    {
        var counts = new Dictionary<string, int> { [BrilliantMinds] = 33, [BlackMirror] = 32, [Untracked] = 5 };
        File.WriteAllText(Path.Combine(_imdbDir, "title.episode.tsv"), EpisodeFixture([BrilliantMinds, BlackMirror, Untracked], counts));
        File.WriteAllText(Path.Combine(_imdbDir, "title.basics.tsv"), BasicsFixture([BrilliantMinds, BlackMirror, Untracked], counts));
        File.WriteAllText(Path.Combine(_imdbDir, "title.akas.tsv"), AkasFixture([BrilliantMinds, Untracked]));
    }

    // title.episode.tsv: tconst, parentTconst, seasonNumber, episodeNumber. BrilliantMinds gets 32 numbered
    // rows plus one \N legacy row (33 total); the others get their count.
    private static string EpisodeFixture(IReadOnlyList<string> parents, Dictionary<string, int> counts)
    {
        var sb = new StringBuilder("tconst\tparentTconst\tseasonNumber\tepisodeNumber\n");
        foreach (var parent in parents)
        {
            var n = counts[parent];
            for (var i = 1; i < n; i++)
            {
                sb.Append($"{parent}_{i:D3}\t{parent}\t{(i + 4) / 5}\t{i}\n");
            }

            // The last row for the first show carries \N season/episode (DATA_ANALYSIS §6).
            var legacy = parent == BrilliantMinds ? "\\N\t\\N" : $"{n}\t{n}";
            sb.Append($"{parent}_legacy\t{parent}\t{legacy}\n");
        }

        return sb.ToString();
    }

    // title.basics.tsv: tconst, titleType, primaryTitle, originalTitle, isAdult, startYear, endYear, runtime, genres.
    private static string BasicsFixture(IReadOnlyList<string> parents, Dictionary<string, int> counts)
    {
        var sb = new StringBuilder("tconst\ttitleType\tprimaryTitle\toriginalTitle\tisAdult\tstartYear\tendYear\truntimeMinutes\tgenres\n");
        foreach (var parent in parents)
        {
            sb.Append($"{parent}\ttvSeries\tShow {parent}\t{parent}\t0\t2011\t\\N\t60\tDrama\n");
            var n = counts[parent];
            for (var i = 1; i < n; i++)
            {
                sb.Append($"{parent}_{i:D3}\ttvEpisode\tEpisode {i}\t\\N\t0\t2024\t\\N\t44\tDrama\n");
            }

            sb.Append($"{parent}_legacy\ttvEpisode\tLegacy\t\\N\t0\t\\N\t\\N\t\\N\tDrama\n");
        }

        // A tvMovie row that must never reach imdb_title or imdb_episode.
        sb.Append("tt000movie\ttvMovie\tSome Film\t\\N\t0\t2000\t\\N\t120\tDrama\n");
        return sb.ToString();
    }

    // title.akas.tsv: titleId, ordering, title, region, language, types, attributes, isOriginalTitle.
    private static string AkasFixture(IReadOnlyList<string> ids)
    {
        var sb = new StringBuilder("titleId\tordering\ttitle\tregion\tlanguage\ttypes\tattributes\tisOriginalTitle\n");
        foreach (var id in ids)
        {
            sb.Append($"{id}\t1\tOriginal {id}\tUS\ten\t\\N\t\\N\t1\n");
            sb.Append($"{id}\t2\tLocalized {id}\tRU\tru\timdb_display\t\\N\t0\n");
        }

        return sb.ToString();
    }

    // ---- helpers ----

    private async Task<int> CountEpisodes(string parent)
    {
        await using var db = CreateContext();
        return await db.ImdbEpisodes.CountAsync(e => e.ParentTconst == parent);
    }

    private async Task<ImdbImportReport> RunImporter(FakeDownloader? downloader = null, bool grantLease = true)
    {
        await using var db = CreateContext();
        var importer = new ImdbImporter(
            db,
            downloader ?? new FakeDownloader(),
            new FakeLease(grantLease),
            _ids,
            CreatePaths(),
            new ShowsOptions { Imdb = { BaseUrl = "https://datasets.imdbws.com" } },
            NullLogger<ImdbImporter>.Instance);
        return await importer.RunAsync(CancellationToken.None);
    }

    private DataPaths CreatePaths()
    {
        var options = Options.Create(new ShowsOptions
        {
            Data = { BaseDirectory = _baseDir },
        });
        return new DataPaths(options);
    }

    private void SeedTracked(string imdbId, int tvmazeId, bool deleted = false)
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
            ImdbId = imdbId,
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
        if (Directory.Exists(_baseDir))
        {
            Directory.Delete(_baseDir, recursive: true);
        }
    }

    // Writes a gzipped fixture to the target path when a dataset is not already on disk. Records whether it
    // was called at all, so the reuse test can assert no download happened.
    private sealed class FakeDownloader : IImdbDatasetDownloader
    {
        private readonly Dictionary<string, string> _contents = new();
        public bool Called { get; private set; }

        public FakeDownloader With(string dataset, string tsvContent)
        {
            _contents[dataset] = tsvContent;
            return this;
        }

        public async Task<DatasetDownloadResult> DownloadAsync(
            string datasetName, string sourceUrl, string targetPath, CancellationToken cancellationToken)
        {
            Called = true;
            if (!_contents.TryGetValue(datasetName, out var content))
            {
                return new DatasetDownloadResult(false, "no fixture", 0);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            await using (var file = File.Create(targetPath))
            await using (var gz = new GZipStream(file, CompressionLevel.Fastest))
            await using (var writer = new StreamWriter(gz, Encoding.UTF8))
            {
                await writer.WriteAsync(content);
            }

            return new DatasetDownloadResult(true, null, new FileInfo(targetPath).Length);
        }
    }

    private sealed class FakeLease(bool grant) : IWriterLease
    {
        public Task<IWriterLeaseHandle?> TryAcquireAsync(string name, CancellationToken cancellationToken) =>
            Task.FromResult<IWriterLeaseHandle?>(grant ? new Handle() : null);

        private sealed class Handle : IWriterLeaseHandle
        {
            public string Name => "imdb_import";
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
