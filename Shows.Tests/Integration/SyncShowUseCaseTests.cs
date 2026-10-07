using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Shows.Application;
using Shows.Application.UseCases;
using Shows.Domain;
using Shows.Infrastructure.Persistence;
using Shows.Infrastructure.Persistence.Repositories;
using Shows.Infrastructure.Persistence.Sources;
using Shows.Infrastructure.Tvmaze;
using Xunit;

namespace Shows.Tests.Integration;

/// <summary>
/// Acceptance for <see cref="SyncShowUseCase"/> (user-issues improvement 7) — the details-page "Sync"
/// button's use case, the single-show analogue of the EpisodeRefresh job. Driven by a fake
/// <see cref="ITvmazeShowSource"/> over the real Black Mirror payload (DATA_ANALYSIS §8), with the real
/// repositories, mapper and unit of work — no network. The assertions are the ones that fail silently when
/// wrong: a sync that does not re-map, a second sync that reports a spurious change (idempotence), a
/// transport failure that corrupts the show's rows, or a sync that regenerates an episode id and orphans a
/// watch mark.
/// </summary>
public sealed class SyncShowUseCaseTests : IClassFixture<DatabaseFixture>, IAsyncLifetime
{
    private const int TvmazeId = 305;

    private readonly DatabaseFixture _fixture;
    private long _showId;

    public SyncShowUseCaseTests(DatabaseFixture fixture) => _fixture = fixture;

    public Task InitializeAsync()
    {
        _fixture.Reset();
        return Task.CompletedTask;
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Sync_re_maps_the_show_and_fills_the_mapped_rows()
    {
        // A tracked-show shell with no mapped fields yet — the state right after AddShowToTracking's
        // EnsureSourceRowAsync but before the first map. Sync must fill it from the fetched payload.
        await SeedShellAsync();

        await using (var context = _fixture.CreateContext())
        {
            var useCase = BuildUseCase(context, new FakeSource(Payload("Black Mirror", "Running")));
            var result = await useCase.ExecuteAsync(_showId, CancellationToken.None);

            Assert.True(result.Changed); // the shell had no title; the map filled it
        }

        await using (var context = _fixture.CreateContext())
        {
            var show = await context.TrackedShows.SingleAsync(s => s.Id == _showId);
            Assert.Equal("Black Mirror", show.Title);
            Assert.Equal(ShowStatus.Running, show.Status);
            // The mapper created the season + episode rows (2 regular + 1 special).
            Assert.Equal(3, await context.Episodes.CountAsync(e => e.ShowId == _showId));
        }
    }

    [Fact]
    public async Task Second_sync_of_an_unchanged_payload_reports_no_change()
    {
        await SeedShellAsync();
        var payload = Payload("Black Mirror", "Running");

        await using (var context = _fixture.CreateContext())
        {
            var useCase = BuildUseCase(context, new FakeSource(payload));
            await useCase.ExecuteAsync(_showId, CancellationToken.None);
        }

        await using (var context = _fixture.CreateContext())
        {
            var useCase = BuildUseCase(context, new FakeSource(payload));
            var second = await useCase.ExecuteAsync(_showId, CancellationToken.None);

            // Idempotence: the same payload maps to the same rows, so items_changed is honestly false.
            Assert.False(second.Changed);
        }

        await using (var context = _fixture.CreateContext())
        {
            // Still exactly three episodes — the second sync did not duplicate them.
            Assert.Equal(3, await context.Episodes.CountAsync(e => e.ShowId == _showId));
        }
    }

    [Fact]
    public async Task Sync_picks_up_a_changed_title_from_tvmaze()
    {
        await SeedShellAsync();
        await using (var context = _fixture.CreateContext())
        {
            var useCase = BuildUseCase(context, new FakeSource(Payload("Black Mirror", "Running")));
            await useCase.ExecuteAsync(_showId, CancellationToken.None);
        }

        // TVmaze renames the show; a sync must surface it.
        await using (var context = _fixture.CreateContext())
        {
            var useCase = BuildUseCase(context, new FakeSource(Payload("Black Mirror (renamed)", "Running")));
            var result = await useCase.ExecuteAsync(_showId, CancellationToken.None);
            Assert.True(result.Changed);
        }

        await using (var context = _fixture.CreateContext())
        {
            Assert.Equal("Black Mirror (renamed)",
                (await context.TrackedShows.SingleAsync(s => s.Id == _showId)).Title);
        }
    }

    [Fact]
    public async Task Transport_failure_throws_and_leaves_the_show_untouched()
    {
        // First sync populates the show.
        await SeedShellAsync();
        await using (var context = _fixture.CreateContext())
        {
            var useCase = BuildUseCase(context, new FakeSource(Payload("Black Mirror", "Running")));
            await useCase.ExecuteAsync(_showId, CancellationToken.None);
        }

        string? titleBefore;
        int episodeCountBefore;
        await using (var context = _fixture.CreateContext())
        {
            titleBefore = (await context.TrackedShows.SingleAsync(s => s.Id == _showId)).Title;
            episodeCountBefore = await context.Episodes.CountAsync(e => e.ShowId == _showId);
        }

        // R14: an unreachable TVmaze throws (never a silent no-change) and the show's rows are unchanged.
        await using (var context = _fixture.CreateContext())
        {
            var useCase = BuildUseCase(context, FakeSource.Unavailable());
            await Assert.ThrowsAsync<TvmazeUnavailableException>(
                () => useCase.ExecuteAsync(_showId, CancellationToken.None));
        }

        await using (var context = _fixture.CreateContext())
        {
            Assert.Equal(titleBefore, (await context.TrackedShows.SingleAsync(s => s.Id == _showId)).Title);
            Assert.Equal(episodeCountBefore, await context.Episodes.CountAsync(e => e.ShowId == _showId));
        }
    }

    [Fact]
    public async Task Sync_preserves_watch_marks_and_episode_ids()
    {
        await SeedShellAsync();
        await using (var context = _fixture.CreateContext())
        {
            var useCase = BuildUseCase(context, new FakeSource(Payload("Black Mirror", "Running")));
            await useCase.ExecuteAsync(_showId, CancellationToken.None);
        }

        // Mark a regular episode watched, then sync again. The mapper must not regenerate the episode id
        // (episode_watch.episode_id points at it) nor touch the user table.
        long episodeId;
        await using (var context = _fixture.CreateContext())
        {
            episodeId = (await context.Episodes
                .Where(e => e.ShowId == _showId && e.EpisodeType == EpisodeType.Regular)
                .OrderBy(e => e.SeasonNumber).ThenBy(e => e.EpisodeNumber)
                .FirstAsync()).Id;
            context.EpisodeWatches.Add(new EpisodeWatch
            {
                Id = 9_500_001,
                ShowId = _showId,
                EpisodeId = episodeId,
                WatchedAt = DateTimeOffset.UtcNow,
            });
            await context.SaveChangesAsync();
        }

        await using (var context = _fixture.CreateContext())
        {
            var useCase = BuildUseCase(context, new FakeSource(Payload("Black Mirror", "Running")));
            await useCase.ExecuteAsync(_showId, CancellationToken.None);
        }

        await using (var context = _fixture.CreateContext())
        {
            var watch = await context.EpisodeWatches.SingleAsync(w => w.ShowId == _showId);
            Assert.Equal(episodeId, watch.EpisodeId); // id stable, mark survived the re-map
        }
    }

    [Fact]
    public async Task Syncing_an_untracked_show_is_refused()
    {
        await using var context = _fixture.CreateContext();
        var useCase = BuildUseCase(context, new FakeSource(Payload("Black Mirror", "Running")));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => useCase.ExecuteAsync(999_999, CancellationToken.None));
    }

    // ---- helpers ----

    private SyncShowUseCase BuildUseCase(ShowsDbContext context, ITvmazeShowSource source)
    {
        // One id generator per build, shared with the mapper, so a re-map that regenerated an id would be
        // detectable (the generator only moves forward).
        var ids = new SequentialIdGenerator();
        var mapper = new TvmazeShowMapper(context, ids, NullLogger<TvmazeShowMapper>.Instance, source);
        return new SyncShowUseCase(
            new TrackedShowRepository(context),
            mapper,
            new UnitOfWork(context));
    }

    // A tracked_show shell + its source-row shell, with a live user_show — the state a sync operates on.
    private async Task SeedShellAsync()
    {
        _showId = 6_000_001;
        await using var context = _fixture.CreateContext();

        context.TvmazeShows.Add(new TvmazeShow
        {
            TvmazeId = TvmazeId,
            StatusRaw = string.Empty,
            RawJson = "{}",
            FetchedAt = DateTimeOffset.UtcNow,
        });

        var show = new TrackedShow { Id = _showId, TvmazeId = TvmazeId };
        context.Entry(show).Property<int>("tvmaze_show_id").CurrentValue = TvmazeId;
        show.User = new UserShow
        {
            TrackedShowId = _showId,
            Category = ShowCategory.Started,
            AddedAt = DateTimeOffset.UtcNow,
            CategoryChangedAt = DateTimeOffset.UtcNow,
        };
        context.TrackedShows.Add(show);
        await context.SaveChangesAsync();
    }

    // The Black Mirror payload from DATA_ANALYSIS.md §8 (2 regular + 1 special), parameterised so a test
    // can simulate a TVmaze-side change.
    private static TvmazePayload Payload(string name, string statusRaw)
    {
        var show = new TvmazeShow
        {
            TvmazeId = TvmazeId,
            Name = name,
            TypeRaw = "Scripted",
            Language = "English",
            StatusRaw = statusRaw,
            RuntimeMinutes = null,
            AverageRuntime = 62,
            Premiered = "2011-12-04",
            Ended = null,
            OfficialSite = "https://www.netflix.com/title/70264888",
            ScheduleTime = "",
            ScheduleDays = "",
            RatingAverage = 8.4,
            NetworkName = null,
            WebchannelName = "Netflix",
            ImdbId = "tt2085059",
            ThetvdbId = 253463,
            GenresRaw = """["Drama","Science-Fiction","Thriller"]""",
            ImageOriginal = "https://static.tvmaze.com/uploads/images/original_untouched/1411764.jpg",
            ImageMedium = "https://static.tvmaze.com/uploads/images/medium_portrait/1411764.jpg",
            SummaryHtml = "<p>Over the last ten years, technology has transformed...</p>",
            ProviderUpdated = 1767985660,
            RawJson = "{}",
            FetchedAt = DateTimeOffset.UtcNow,
        };

        var episodes = new List<TvmazeEpisode>
        {
            new()
            {
                TvmazeEpisodeId = 30902, TvmazeShowId = TvmazeId, Name = "The National Anthem",
                SeasonNumber = 1, Number = 1, TypeRaw = "regular",
                Airdate = "2011-12-04", Airstamp = "2011-12-04T21:00:00+00:00", RuntimeMinutes = 60,
                SummaryHtml = "<p>Prime Minister Michael Callow faces a huge and shocking dilemma...</p>",
                RawJson = "{}", FetchedAt = DateTimeOffset.UtcNow,
            },
            new()
            {
                TvmazeEpisodeId = 30903, TvmazeShowId = TvmazeId, Name = "Fifteen Million Merits",
                SeasonNumber = 1, Number = 2, TypeRaw = "regular",
                Airdate = "2011-12-11", Airstamp = "2011-12-11T21:00:00+00:00", RuntimeMinutes = 62,
                SummaryHtml = "<p>Bing wakes up in a strange virtual world...</p>",
                RawJson = "{}", FetchedAt = DateTimeOffset.UtcNow,
            },
            new()
            {
                TvmazeEpisodeId = 30904, TvmazeShowId = TvmazeId, Name = "Special",
                SeasonNumber = 0, Number = 1, TypeRaw = "special",
                Airdate = "2014-12-16", Airstamp = "2014-12-16T00:00:00+00:00",
                RawJson = "{}", FetchedAt = DateTimeOffset.UtcNow,
            },
        };

        return new TvmazePayload(show, episodes);
    }

    private sealed class SequentialIdGenerator : IIdGenerator
    {
        private long _next = 1_000_000;
        public long NextId() => _next++;
    }

    private sealed class FakeSource(TvmazePayload? payload) : ITvmazeShowSource
    {
        private readonly bool _unavailable;

        private FakeSource() : this(null) => _unavailable = true;

        public static FakeSource Unavailable() => new();

        public Task<TvmazePayload?> FetchAsync(int tvmazeId, CancellationToken cancellationToken)
        {
            if (_unavailable)
            {
                throw new TvmazeUnavailableException("tvmaze.example unreachable", new IOException("reset"));
            }

            return Task.FromResult(payload);
        }
    }
}
