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
/// Improvement 21 acceptance for the <c>Enrichment --country</c> job: the country of every actively-tracked
/// show is re-derived from the payload <b>already stored</b> in <c>tvmaze_show.raw_json</c>, with no network
/// call at all. The assertions that matter are the ones a naive implementation would pass while being wrong:
/// that the source columns stay <b>verbatim</b> (no language written into them, SPEC R11), that a re-run
/// reports zero changes, and that a soft-deleted show is never rewritten (R8).
/// </summary>
public sealed class CountryBackfillJobTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SequentialIdGenerator _ids = new();

    public CountryBackfillJobTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"shows-country-{Guid.NewGuid():N}.db");
        using var context = CreateContext();
        context.Database.EnsureCreated();
    }

    [Fact]
    public async Task A_stored_broadcaster_country_fills_both_layers()
    {
        // A payload captured before the country columns existed: the source row has the nested country only
        // inside raw_json, so both layers must be filled from it.
        SeedTracked(305, language: "English", rawJson: ShowPayload(
            """{"name":"NBC","country":{"name":"United States","code":"US"}}""", webChannel: null));

        var report = await RunJob();

        Assert.False(report.Refused);
        Assert.Equal(1, report.Changed);

        await using var db = CreateContext();
        var show = db.TrackedShows.Single(s => s.TvmazeId == 305);
        var source = db.TvmazeShows.Single(s => s.TvmazeId == 305);

        Assert.Equal("United States", source.CountryName);
        Assert.Equal("US", source.CountryCode);
        Assert.Equal("United States", show.Country);
    }

    [Fact]
    public async Task A_show_with_no_country_but_a_language_gets_the_language_as_its_country()
    {
        // The user's case (tvmaze 84377): Netflix with country null, language Korean. The mapped column
        // reads "Korean"; the source columns stay null because TVmaze never said "Korea".
        SeedTracked(84377, language: "Korean", rawJson: ShowPayload(
            webChannel: """{"name":"Netflix","country":null}""", network: null));

        await RunJob();

        await using var db = CreateContext();
        var show = db.TrackedShows.Single(s => s.TvmazeId == 84377);
        var source = db.TvmazeShows.Single(s => s.TvmazeId == 84377);

        Assert.Equal("Korean", show.Country);
        Assert.Null(source.CountryName);   // SPEC R11: the source table is a copy of the payload
        Assert.Null(source.CountryCode);
    }

    [Fact]
    public async Task A_show_with_neither_country_nor_language_stays_null()
    {
        SeedTracked(42, language: null, rawJson: ShowPayload(network: null, webChannel: null));

        await RunJob();

        await using var db = CreateContext();
        Assert.Null(db.TrackedShows.Single(s => s.TvmazeId == 42).Country);
    }

    [Fact]
    public async Task Re_running_reports_zero_changes()
    {
        SeedTracked(305, language: "English", rawJson: ShowPayload(
            """{"name":"NBC","country":{"name":"United States","code":"US"}}""", webChannel: null));

        await RunJob();
        var second = await RunJob();

        // Every write is a guarded comparison, so a second pass over the same payloads is a no-op. This is
        // what makes the mode safe to leave in the job permanently rather than as a one-off script.
        Assert.Equal(0, second.Changed);
    }

    [Fact]
    public async Task Soft_deleted_show_is_never_rewritten()
    {
        SeedTracked(700, language: "Korean", rawJson: ShowPayload(
            webChannel: """{"name":"Netflix","country":null}""", network: null), deleted: true);
        SeedTracked(701, language: "Korean", rawJson: ShowPayload(
            webChannel: """{"name":"Netflix","country":null}""", network: null));

        var report = await RunJob();

        await using var db = CreateContext();
        Assert.Null(db.TrackedShows.Single(s => s.TvmazeId == 700).Country); // R8: not a target
        Assert.Equal("Korean", db.TrackedShows.Single(s => s.TvmazeId == 701).Country);
        Assert.Equal(1, report.Total); // only the active show was selected
    }

    [Fact]
    public async Task An_unparseable_payload_keeps_the_values_already_stored()
    {
        // A shell row (raw_json = "{}") or a truncated capture must not clear a country we already know.
        // Losing a known value to a parsing gap is the worse error (the same rule ScheduleMerge follows).
        SeedTracked(500, language: "English", rawJson: "{ this is not json", existingCountry: "France");

        await RunJob();

        await using var db = CreateContext();
        Assert.Equal("France", db.TrackedShows.Single(s => s.TvmazeId == 500).Country);
    }

    [Fact]
    public async Task Refuses_when_another_writer_holds_the_lease()
    {
        SeedTracked(305, language: "Korean", rawJson: ShowPayload(
            webChannel: """{"name":"Netflix","country":null}""", network: null));

        var report = await RunJob(grantLease: false);

        Assert.True(report.Refused);
        await using var db = CreateContext();
        Assert.Null(db.TrackedShows.Single(s => s.TvmazeId == 305).Country);
    }

    [Fact]
    public async Task The_run_is_recorded_with_its_totals_and_changes()
    {
        SeedTracked(305, language: "Korean", rawJson: ShowPayload(
            webChannel: """{"name":"Netflix","country":null}""", network: null));

        await RunJob();

        await using var db = CreateContext();
        var run = db.JobRuns.Single(r => r.JobName == "Enrichment.Country");
        Assert.Equal(1, run.ItemsTotal);
        Assert.Equal(1, run.ItemsChanged);
        Assert.True(run.Succeeded);
        Assert.NotNull(run.FinishedAt);
    }

    // ---- helpers ----

    // A minimal show payload with just the broadcaster shape under test. TVmaze puts the country one level
    // inside whichever broadcaster exists, so the fixture mirrors that nesting exactly. The language is not
    // in the payload on purpose: the re-map reads the language from the source column the original map
    // already filled, not by re-parsing — the payload is read only for what was never stored.
    private static string ShowPayload(string? network, string? webChannel) =>
        """{"network":""" + (network ?? "null") + ""","webChannel":""" + (webChannel ?? "null") + "}";

    private async Task<CountryBackfillReport> RunJob(bool grantLease = true)
    {
        await using var db = CreateContext();
        var job = new CountryBackfillJob(
            db,
            new TrackedShowRepository(db),
            new TvmazeShowMapper(db, _ids, NullLogger<TvmazeShowMapper>.Instance),
            new FakeLease(grantLease),
            _ids,
            new ShowsOptions(),
            NullLogger<CountryBackfillJob>.Instance);
        return await job.RunAsync(CancellationToken.None);
    }

    private void SeedTracked(
        int tvmazeId, string? language, string rawJson, bool deleted = false, string? existingCountry = null)
    {
        using var db = CreateContext();

        db.TvmazeShows.Add(new TvmazeShow
        {
            TvmazeId = tvmazeId,
            StatusRaw = "Ended",
            Language = language,
            RawJson = rawJson,
            FetchedAt = DateTimeOffset.UtcNow,
        });

        var show = new TrackedShow
        {
            Id = 20_000_000 + tvmazeId,
            TvmazeId = tvmazeId,
            Status = ShowStatus.Ended,
            Title = $"Show {tvmazeId}",
            Language = language,
            Country = existingCountry,
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

    private sealed class FakeLease(bool grant) : IWriterLease
    {
        public Task<IWriterLeaseHandle?> TryAcquireAsync(string name, CancellationToken cancellationToken) =>
            Task.FromResult<IWriterLeaseHandle?>(grant ? new Handle() : null);

        private sealed class Handle : IWriterLeaseHandle
        {
            public string Name => "enrichment_country";
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
