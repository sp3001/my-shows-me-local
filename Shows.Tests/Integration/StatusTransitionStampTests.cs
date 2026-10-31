using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Shows.Application;
using Shows.Domain;
using Shows.Infrastructure.Persistence;
using Shows.Infrastructure.Persistence.Sources;
using Xunit;

namespace Shows.Tests.Integration;

/// <summary>
/// ADR-038 acceptance for the status-transition stamps the Updates tab reads. The assertions are the ones
/// that fail silently when wrong: a show mapped as Ended for the first time that gets today's date instead
/// of its finale's, a renewal that never lands, or a re-map of an unchanged show that re-stamps the date
/// and makes <c>items_changed</c> lie on every refresh run.
/// </summary>
public sealed class StatusTransitionStampTests : IDisposable
{
    private const int TvmazeId = 305;

    private readonly string _dbPath;

    // One id source for the whole test: the mapper mints season/episode ids, and a fresh generator per map
    // would re-mint the same base and collide on the second map (the standing fixture rule).
    private readonly SequentialIdGenerator _ids = new();

    public StatusTransitionStampTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"shows-adr038-{Guid.NewGuid():N}.db");
        using var context = CreateContext();
        context.Database.EnsureCreated();
    }

    [Fact]
    public async Task First_map_of_an_Ended_show_dates_the_end_from_its_last_aired_episode()
    {
        // The case the user called out explicitly: this is not just the import backfill. A show tracked
        // today whose finale aired in 2019 must read 2019, or the Ended section would claim it ended now.
        await SeedShellAsync(statusRaw: "Ended");
        await MapAsync(Payload("Ended", ended: "2019-06-05"));

        var show = await ReadShowAsync();
        Assert.Equal(ShowStatus.Ended, show.Status);
        // Compared as a UTC instant: the provider stores these as TEXT and the stored offset is the
        // provider's, so an equality check on the raw value is the standing round-trip trap in this repo.
        Assert.Equal(new DateTime(2014, 12, 16, 0, 0, 0, DateTimeKind.Utc), show.StatusChangedAt!.Value.UtcDateTime);
        Assert.Null(show.StatusRenewedAt);
    }

    [Fact]
    public async Task A_running_show_gets_no_end_date_and_no_renewal()
    {
        // A new airing show has no prior status, so neither transition applies. Stamping a renewal here
        // would put every newly-added show into the Renewed section.
        await SeedShellAsync(statusRaw: "Running");
        await MapAsync(Payload("Running"));

        var show = await ReadShowAsync();
        Assert.Equal(ShowStatus.Running, show.Status);
        Assert.Null(show.StatusChangedAt);
        Assert.Null(show.StatusRenewedAt);
    }

    [Fact]
    public async Task Ending_a_show_that_was_Running_stamps_the_end_from_the_last_aired_episode()
    {
        await SeedShellAsync(statusRaw: "Running");
        await MapAsync(Payload("Running"));
        Assert.Null((await ReadShowAsync()).StatusChangedAt);

        await MapAsync(Payload("Ended", ended: "2014-12-16"));

        var show = await ReadShowAsync();
        Assert.Equal(new DateTime(2014, 12, 16, 0, 0, 0, DateTimeKind.Utc), show.StatusChangedAt!.Value.UtcDateTime);
        Assert.Null(show.StatusRenewedAt);
    }

    [Fact]
    public async Task Renewing_an_Ended_show_stamps_the_renewal_at_observation_time()
    {
        await SeedShellAsync(statusRaw: "Ended");
        await MapAsync(Payload("Ended", ended: "2019-06-05"));
        var endedAt = (await ReadShowAsync()).StatusChangedAt;

        var before = DateTimeOffset.UtcNow;
        await MapAsync(Payload("Running"));

        var show = await ReadShowAsync();
        Assert.NotNull(show.StatusRenewedAt);
        Assert.InRange(show.StatusRenewedAt!.Value, before.AddSeconds(-5), DateTimeOffset.UtcNow.AddSeconds(5));

        // The end date survives: it is history, and the Renewed section needs the show to still carry the
        // fact that it had ended.
        Assert.Equal(endedAt, show.StatusChangedAt);
    }

    [Fact]
    public async Task A_show_that_re_ends_takes_a_fresh_end_date_and_leaves_the_renewal_in_place()
    {
        // The case a "previous status" column could not express: after re-ending, the show must read as
        // ended again (so it reappears in Ended) while the renewal it had in between stays recorded.
        //
        // The end date is RECOMPUTED on re-entry, not carried over — and because it is derived from the
        // last aired episode, it moves only when the show actually aired something later. A show that
        // re-ends with the same finale keeps that finale's date, which is the whole point of dating an end
        // from the episode rather than from the observation.
        await SeedShellAsync(statusRaw: "Ended");
        await MapAsync(Payload("Ended", ended: "2019-06-05"));
        var firstEnd = (await ReadShowAsync()).StatusChangedAt;

        await MapAsync(Payload("Running"));
        var renewed = (await ReadShowAsync()).StatusRenewedAt;
        Assert.NotNull(renewed);

        // Re-end with no new episode: same finale, so the same date. Not a stale carry-over — a recompute
        // that agrees.
        await MapAsync(Payload("Ended", ended: "2019-06-05"));
        Assert.Equal(firstEnd, (await ReadShowAsync()).StatusChangedAt);

        // Re-end after a new season aired: the later finale is now the end. The second Running map is a
        // second renewal, which legitimately refreshes StatusRenewedAt — the column means "the last time
        // we saw it come back", not "the first time".
        await MapAsync(Payload("Running"));
        var secondRenewal = (await ReadShowAsync()).StatusRenewedAt;
        Assert.NotNull(secondRenewal);
        Assert.True(secondRenewal!.Value >= renewed!.Value);

        await MapAsync(Payload("Ended", ended: "2021-01-01", episodeCount: 3));

        var show = await ReadShowAsync();
        Assert.Equal(ShowStatus.Ended, show.Status);
        Assert.Equal(new DateTime(2021, 1, 1, 0, 0, 0, DateTimeKind.Utc), show.StatusChangedAt!.Value.UtcDateTime);
        Assert.Equal(secondRenewal!.Value.UtcDateTime, show.StatusRenewedAt!.Value.UtcDateTime);
    }

    [Fact]
    public async Task Canceled_counts_as_terminal_and_is_not_reported_as_a_renewal()
    {
        // The user's instruction: a cancelled show is in fact ended. And Ended -> Canceled is a
        // cancellation, not a renewal, so it must not light up the Renewed section.
        await SeedShellAsync(statusRaw: "Ended");
        await MapAsync(Payload("Ended", ended: "2019-06-05"));

        await MapAsync(CanceledPayload());

        var show = await ReadShowAsync();
        Assert.Equal(ShowStatus.Canceled, show.Status);
        Assert.Null(show.StatusRenewedAt);
        Assert.True(ShowStatusRules.NoMoreEpisodesExpected(show.Status));
    }

    [Fact]
    public async Task Leaving_terminal_into_Unknown_is_not_reported_as_a_renewal()
    {
        // R12 fail-closed: an unmapped vocabulary value is a dictionary gap, not news. Reporting it as a
        // renewal would invent renewals every time TVmaze adds a status the dictionary has not caught up to.
        await SeedShellAsync(statusRaw: "Ended");
        await MapAsync(Payload("Ended", ended: "2019-06-05"));

        var payload = Payload("On Hiatus");
        payload.Show.StatusRaw = "On Hiatus";
        await MapAsync(payload);

        var show = await ReadShowAsync();
        Assert.Equal(ShowStatus.Unknown, show.Status);
        Assert.Null(show.StatusRenewedAt);
    }

    [Fact]
    public async Task Remapping_an_unchanged_Ended_show_does_not_re_stamp_the_end()
    {
        // Idempotence (step 1.5): StatusRecheck re-polls Ended shows every run. If the end date moved on
        // each map, every run would report a change and the Ended section's order would drift.
        await SeedShellAsync(statusRaw: "Ended");
        await MapAsync(Payload("Ended", ended: "2019-06-05"));
        var first = (await ReadShowAsync()).StatusChangedAt;

        var second = await MapAsync(Payload("Ended", ended: "2019-06-05"));

        Assert.False(second.AnyChange);
        Assert.Equal(first, (await ReadShowAsync()).StatusChangedAt);
    }

    [Fact]
    public async Task An_Ended_show_with_no_dated_episode_falls_back_to_now_once_and_then_holds()
    {
        // The fallback must be written ONCE. If it re-stamped every map, an episode-less terminal show
        // would report a change on every refresh run forever.
        await SeedShellAsync(statusRaw: "Ended");
        await MapAsync(Payload("Ended", ended: "2019-06-05", episodeCount: 0));

        var first = (await ReadShowAsync()).StatusChangedAt;
        Assert.NotNull(first);
        Assert.InRange(first!.Value, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddMinutes(5));

        var second = await MapAsync(Payload("Ended", ended: "2019-06-05", episodeCount: 0));
        Assert.False(second.AnyChange);
        Assert.Equal(first, (await ReadShowAsync()).StatusChangedAt);
    }

    [Fact]
    public async Task A_placeholder_end_date_is_replaced_once_the_episodes_arrive()
    {
        // The self-heal that keeps the column honest without the backfill: the placeholder written above is
        // evidence-free, so when EpisodeRefresh brings the finale the real air date wins.
        await SeedShellAsync(statusRaw: "Ended");
        await MapAsync(Payload("Ended", ended: "2019-06-05", episodeCount: 0));
        var placeholder = (await ReadShowAsync()).StatusChangedAt;

        await MapAsync(Payload("Ended", ended: "2019-06-05"));

        var show = await ReadShowAsync();
        Assert.NotEqual(placeholder, show.StatusChangedAt);
        Assert.Equal(new DateTime(2014, 12, 16, 0, 0, 0, DateTimeKind.Utc), show.StatusChangedAt!.Value.UtcDateTime);
    }

    [Fact]
    public async Task Tvmaze_updated_at_mirrors_the_source_provider_updated()
    {
        // The mapped copy the Updated Lately section reads (ADR-038). The UI cannot read
        // tvmaze_show.provider_updated (source-table containment), so this column is how it crosses.
        await SeedShellAsync(statusRaw: "Running");
        var payload = Payload("Running");
        payload.Show.ProviderUpdated = 1767985660;
        await MapAsync(payload);

        var show = await ReadShowAsync();
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1767985660).UtcDateTime,
            show.TvmazeUpdatedAt!.Value.UtcDateTime);
    }

    [Fact]
    public async Task A_null_provider_updated_leaves_tvmaze_updated_at_null()
    {
        // A shell source row has no 'updated' field. Null must stay null, not become the epoch, or the
        // Updated section would show every never-fetched show as updated in 1970.
        await SeedShellAsync(statusRaw: "Running");
        var payload = Payload("Running");
        payload.Show.ProviderUpdated = null;
        await MapAsync(payload);

        Assert.Null((await ReadShowAsync()).TvmazeUpdatedAt);
    }

    // ---- helpers ----

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath))
        {
            File.Delete(_dbPath);
        }
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

    private async Task<MapResult> MapAsync(TvmazePayload payload)
    {
        await using var context = CreateContext();
        var mapper = new TvmazeShowMapper(context, _ids, NullLogger<TvmazeShowMapper>.Instance);
        var result = await mapper.MapShowAsync(payload, default);
        await context.SaveChangesAsync();
        return result;
    }

    private async Task<TrackedShow> ReadShowAsync()
    {
        await using var context = CreateContext();
        return await context.TrackedShows.SingleAsync(s => s.TvmazeId == TvmazeId);
    }

    private async Task SeedShellAsync(string statusRaw)
    {
        await using var context = CreateContext();

        // tracked_show.tvmaze_show_id is a NOT NULL FK, so the shell needs a source row to point at
        // (SCHEMA §2). The status is seeded on the SOURCE row only — the mapped row starts at Unknown,
        // exactly as a real first map sees it, which is what makes the first-map test mean what it says.
        context.TvmazeShows.Add(new TvmazeShow
        {
            TvmazeId = TvmazeId,
            StatusRaw = statusRaw,
            RawJson = "{}",
            FetchedAt = DateTimeOffset.UtcNow,
        });

        const long showId = 8_100_001;
        var show = new TrackedShow { Id = showId, TvmazeId = TvmazeId };
        context.Entry(show).Property("tvmaze_show_id").CurrentValue = TvmazeId;
        show.User = new UserShow
        {
            TrackedShowId = showId,
            Category = ShowCategory.Started,
            AddedAt = DateTimeOffset.UtcNow,
            CategoryChangedAt = DateTimeOffset.UtcNow,
        };
        context.TrackedShows.Add(show);
        await context.SaveChangesAsync();
    }

    // Two dated episodes, the later one a season-0 special at 2014-12-16 — deliberately the newest, so a
    // rule that meant "last regular" rather than "last aired" would compute a different instant here.
    /// <param name="episodeCount">0 for a show with no dated episode at all — the case the end-date
    /// fallback exists for. The default 2 keeps the newest episode a season-0 special, so a rule that
    /// meant "last regular" rather than "last aired" computes a different instant.</param>
    private static TvmazePayload Payload(string statusRaw, string? ended = null, int episodeCount = 2)
    {
        var show = new TvmazeShow
        {
            TvmazeId = TvmazeId,
            Name = "Black Mirror",
            TypeRaw = "Scripted",
            Language = "English",
            StatusRaw = statusRaw,
            AverageRuntime = 62,
            Premiered = "2011-12-04",
            Ended = ended,
            ImdbId = "tt2085059",
            RawJson = "{}",
            FetchedAt = DateTimeOffset.UtcNow,
        };

        var episodes = new List<TvmazeEpisode>
        {
            new()
            {
                TvmazeEpisodeId = 30902,
                TvmazeShowId = TvmazeId,
                Name = "The National Anthem",
                SeasonNumber = 1,
                Number = 1,
                TypeRaw = "regular",
                Airdate = "2011-12-04",
                Airstamp = "2011-12-04T21:00:00+00:00",
                RawJson = "{}",
                FetchedAt = DateTimeOffset.UtcNow,
            },
            new()
            {
                TvmazeEpisodeId = 30904,
                TvmazeShowId = TvmazeId,
                Name = "Special",
                SeasonNumber = 0,
                Number = 1,
                TypeRaw = "significant_special",
                Airdate = "2014-12-16",
                Airstamp = "2014-12-16T00:00:00+00:00",
                RawJson = "{}",
                FetchedAt = DateTimeOffset.UtcNow,
            },
        };

        // 3 = the two above plus a 2021 finale, so a re-end after a renewal has a later episode to date from.
        if (episodeCount == 3)
        {
            episodes.Add(new TvmazeEpisode
            {
                TvmazeEpisodeId = 30999,
                TvmazeShowId = TvmazeId,
                Name = "Salt in the Wound",
                SeasonNumber = 7,
                Number = 6,
                TypeRaw = "regular",
                Airdate = "2021-01-01",
                Airstamp = "2021-01-01T00:00:00+00:00",
                RawJson = "{}",
                FetchedAt = DateTimeOffset.UtcNow,
            });
        }

        return new TvmazePayload(show, episodeCount switch
        {
            0 => [],
            3 => episodes,
            _ => episodes.Take(2).ToList(),
        });
    }

    private static TvmazePayload CanceledPayload()
    {
        var payload = Payload("Canceled", ended: "2019-06-05");
        payload.Show.StatusRaw = "Canceled";
        return payload;
    }

    private sealed class SequentialIdGenerator : IIdGenerator
    {
        private long _next = 8_000_000_000;

        public long NextId() => Interlocked.Increment(ref _next);
    }
}
