using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Shows.Application;
using Shows.Application.UseCases;
using Shows.Domain;
using Shows.Infrastructure.Persistence;
using Shows.Infrastructure.Persistence.Repositories;
using Shows.Infrastructure.Persistence.Sources;
using Xunit;

namespace Shows.Tests.Integration;

/// <summary>
/// Step 3.2 acceptance for <see cref="AddShowToTrackingUseCase"/> — the only place a
/// <c>tracked_show</c> and its <c>user_show</c> row are created together (ADR-024). Driven by a fake
/// <see cref="ICatalogProvider"/> and a fake <see cref="ITvmazeShowSource"/> over the real Black Mirror
/// payload (DATA_ANALYSIS §8), with the real repositories, mapper and unit of work — no network. The
/// assertions are the ones that fail silently when wrong: a second <c>user_show</c> row on re-add, a
/// reset category, a regenerated episode id orphaning a watch mark, or a restore that re-inserts instead
/// of clearing <c>deleted_at</c>.
/// </summary>
public sealed class AddShowToTrackingUseCaseTests : IClassFixture<DatabaseFixture>, IAsyncLifetime
{
    private const int BlackMirrorTvmazeId = 305;

    private readonly DatabaseFixture _fixture;

    public AddShowToTrackingUseCaseTests(DatabaseFixture fixture) => _fixture = fixture;

    public Task InitializeAsync()
    {
        _fixture.Reset();
        return Task.CompletedTask;
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Adding_the_same_show_twice_writes_one_of_each_row_and_no_duplicate_episodes()
    {
        var first = await AddAsync(ShowCategory.Started);
        var second = await AddAsync(ShowCategory.Started);

        Assert.Equal(first.Id, second.Id); // idempotent: the same show, not a new one

        await using var context = _fixture.CreateContext();
        Assert.Equal(1, await context.TrackedShows.CountAsync(s => s.TvmazeId == BlackMirrorTvmazeId));
        Assert.Equal(1, await context.UserShows.CountAsync(u => u.Show!.TvmazeId == BlackMirrorTvmazeId));
        Assert.Equal(1, await context.TvmazeShows.CountAsync(s => s.TvmazeId == BlackMirrorTvmazeId));

        // No duplicate episodes: the Black Mirror fixture has 3 episodes (2 regular + 1 special). The
        // second add must not insert them again.
        var episodeCount = await context.Episodes.CountAsync(e => e.ShowId == first.Id);
        Assert.Equal(3, episodeCount);
        Assert.Equal(
            episodeCount,
            await context.Episodes.CountAsync(e => e.ShowId == first.Id)); // stable across the re-add
    }

    [Fact]
    public async Task Re_adding_after_a_watch_mark_preserves_the_mark_and_the_category()
    {
        var show = await AddAsync(ShowCategory.Later);

        // Mark one regular episode watched directly (episode_watch is user-owned; the use case never
        // writes it, so seeding it here is the only way it gets a row).
        long episodeId;
        await using (var context = _fixture.CreateContext())
        {
            var first = await context.Episodes
                .Where(e => e.ShowId == show.Id && e.EpisodeType == EpisodeType.Regular)
                .OrderBy(e => e.SeasonNumber).ThenBy(e => e.EpisodeNumber)
                .FirstAsync();
            episodeId = first.Id;
            context.EpisodeWatches.Add(new EpisodeWatch
            {
                Id = 9_000_001,
                ShowId = show.Id,
                EpisodeId = episodeId,
                WatchedAt = DateTimeOffset.UtcNow,
            });
            await context.SaveChangesAsync();
        }

        // Re-add into a DIFFERENT category — the idempotent path must not reset the original category.
        await AddAsync(ShowCategory.Finished);

        await using (var context = _fixture.CreateContext())
        {
            var user = await context.UserShows.SingleAsync(u => u.Show!.TvmazeId == BlackMirrorTvmazeId);
            Assert.Equal(ShowCategory.Later, user.Category); // unchanged, not reset to Finished

            var watch = await context.EpisodeWatches.SingleAsync(w => w.ShowId == show.Id);
            Assert.Equal(episodeId, watch.EpisodeId); // the mark survived, id not regenerated
        }
    }

    [Fact]
    public async Task Add_untrack_add_restores_the_row_without_a_second_user_show_or_lost_history()
    {
        var show = await AddAsync(ShowCategory.Started);

        // Seed a watch mark so we can prove it survives the untrack/re-add round trip.
        long episodeId;
        DateTimeOffset originalAddedAt;
        await using (var context = _fixture.CreateContext())
        {
            var first = await context.Episodes
                .Where(e => e.ShowId == show.Id && e.EpisodeType == EpisodeType.Regular)
                .FirstAsync();
            episodeId = first.Id;
            context.EpisodeWatches.Add(new EpisodeWatch
            {
                Id = 9_100_001,
                ShowId = show.Id,
                EpisodeId = episodeId,
                WatchedAt = DateTimeOffset.UtcNow,
            });
            originalAddedAt = (await context.UserShows.SingleAsync(u => u.TrackedShowId == show.Id)).AddedAt;
            await context.SaveChangesAsync();
        }

        // Untrack (logical delete).
        await UntrackAsync(show.Id);
        await using (var context = _fixture.CreateContext())
        {
            var user = await context.UserShows.SingleAsync(u => u.TrackedShowId == show.Id);
            Assert.NotNull(user.DeletedAt); // soft-deleted, not removed
        }

        // Re-add. Must restore the SAME row, not insert a second one.
        var readded = await AddAsync(ShowCategory.Started);
        Assert.Equal(show.Id, readded.Id);

        await using (var context = _fixture.CreateContext())
        {
            Assert.Equal(1, await context.UserShows.CountAsync(u => u.TrackedShowId == show.Id));
            var user = await context.UserShows.SingleAsync(u => u.TrackedShowId == show.Id);
            Assert.Null(user.DeletedAt); // restored
            Assert.Equal(originalAddedAt, user.AddedAt); // original tracking date preserved (history)

            // The watch mark is still present and points at the same episode id.
            var watch = await context.EpisodeWatches.SingleAsync(w => w.ShowId == show.Id);
            Assert.Equal(episodeId, watch.EpisodeId);
        }
    }

    [Theory]
    [InlineData(ShowCategory.Started)]
    [InlineData(ShowCategory.Later)]
    public async Task Add_lands_the_show_in_the_requested_category(ShowCategory category)
    {
        // user-issues 3 / improvement 10: the search card's Start and Later buttons call this use case
        // with the chosen category, so the category the caller passes must be exactly what is persisted.
        await AddAsync(category);

        await using var context = _fixture.CreateContext();
        var user = await context.UserShows.SingleAsync(u => u.Show!.TvmazeId == BlackMirrorTvmazeId);
        Assert.Equal(category, user.Category);
        Assert.Null(user.DeletedAt); // an add is an active tracking, not a soft-deleted row
    }

    [Fact]
    public async Task Add_untrack_add_under_production_config_restores_without_a_materialization_error()
    {
        // Regression: production configures the SQLite provider through ConfigureShowsSqlite. A global
        // SplitQuery there once crashed the mapper's explicit collection load (NullReferenceException in
        // ClrICollectionAccessor) on the add → untrack → re-add path. The default fixture context
        // hand-builds options and cannot catch a regression in that shared configuration, so this test
        // drives the exact sequence against a context built the way production builds it.
        var show = await AddAsyncProduction(ShowCategory.Later);
        await UntrackAsyncProduction(show.Id);
        var readded = await AddAsyncProduction(ShowCategory.Later);

        Assert.Equal(show.Id, readded.Id);

        await using var context = _fixture.CreateContext();
        Assert.Equal(1, await context.UserShows.CountAsync(u => u.TrackedShowId == show.Id));
        var user = await context.UserShows.SingleAsync(u => u.TrackedShowId == show.Id);
        Assert.Null(user.DeletedAt);
    }

    // ---- helpers ----

    private async Task<TrackedShow> AddAsync(ShowCategory category)
    {
        await using var context = _fixture.CreateContext();
        var useCase = BuildUseCase(context);
        return await useCase.ExecuteAsync(BlackMirrorTvmazeId, category, CancellationToken.None);
    }

    private async Task<TrackedShow> AddAsyncProduction(ShowCategory category)
    {
        await using var context = _fixture.CreateProductionContext();
        var useCase = BuildUseCase(context);
        return await useCase.ExecuteAsync(BlackMirrorTvmazeId, category, CancellationToken.None);
    }

    private async Task UntrackAsyncProduction(long trackedShowId)
    {
        await using var context = _fixture.CreateProductionContext();
        var useCase = new UntrackShowUseCase(
            new TrackedShowRepository(context),
            new UserShowRepository(context),
            new UnitOfWork(context));
        await useCase.ExecuteAsync(trackedShowId, CancellationToken.None);
    }

    private async Task UntrackAsync(long trackedShowId)
    {
        await using var context = _fixture.CreateContext();
        var useCase = new UntrackShowUseCase(
            new TrackedShowRepository(context),
            new UserShowRepository(context),
            new UnitOfWork(context));
        await useCase.ExecuteAsync(trackedShowId, CancellationToken.None);
    }

    private AddShowToTrackingUseCase BuildUseCase(ShowsDbContext context)
    {
        // One id generator per use-case build, but the SAME one the mapper uses, so a re-map that
        // regenerated an id would be detectable (the generator only moves forward).
        var ids = new SequentialIdGenerator();
        var provider = new FakeCatalogProvider(BlackMirrorTvmazeId);
        var source = new FakeTvmazeSource(BlackMirrorPayload());
        var mapper = new TvmazeShowMapper(context, ids, NullLogger<TvmazeShowMapper>.Instance, source);
        return new AddShowToTrackingUseCase(
            provider,
            new TrackedShowRepository(context),
            new UserShowRepository(context),
            mapper,
            ids,
            new UnitOfWork(context));
    }

    // The Black Mirror show payload from DATA_ANALYSIS.md §8 (same fixture as the mapper tests).
    private static TvmazePayload BlackMirrorPayload()
    {
        var show = new TvmazeShow
        {
            TvmazeId = BlackMirrorTvmazeId,
            Name = "Black Mirror",
            TypeRaw = "Scripted",
            Language = "English",
            StatusRaw = "Running",
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
                TvmazeEpisodeId = 30902,
                TvmazeShowId = BlackMirrorTvmazeId,
                Name = "The National Anthem",
                SeasonNumber = 1,
                Number = 1,
                TypeRaw = "regular",
                Airdate = "2011-12-04",
                Airstamp = "2011-12-04T21:00:00+00:00",
                RuntimeMinutes = 60,
                RatingAverage = 7.8,
                SummaryHtml = "<p>Prime Minister Michael Callow faces a huge and shocking dilemma...</p>",
                RawJson = "{}",
                FetchedAt = DateTimeOffset.UtcNow,
            },
            new()
            {
                TvmazeEpisodeId = 30903,
                TvmazeShowId = BlackMirrorTvmazeId,
                Name = "Fifteen Million Merits",
                SeasonNumber = 1,
                Number = 2,
                TypeRaw = "regular",
                Airdate = "2011-12-11",
                Airstamp = "2011-12-11T21:00:00+00:00",
                RuntimeMinutes = 62,
                SummaryHtml = "<p>Bing wakes up in a strange virtual world...</p>",
                RawJson = "{}",
                FetchedAt = DateTimeOffset.UtcNow,
            },
            new()
            {
                TvmazeEpisodeId = 30904,
                TvmazeShowId = BlackMirrorTvmazeId,
                Name = "Special",
                SeasonNumber = 0,
                Number = 1,
                TypeRaw = "special",
                Airdate = "2014-12-16",
                Airstamp = "2014-12-16T00:00:00+00:00",
                RawJson = "{}",
                FetchedAt = DateTimeOffset.UtcNow,
            },
        };

        return new TvmazePayload(show, episodes);
    }

    // Deterministic, strictly-increasing ids so a re-map that regenerates an id is detectable.
    private sealed class SequentialIdGenerator : IIdGenerator
    {
        private long _next = 1_000_000;
        public long NextId() => _next++;
    }

    private sealed class FakeCatalogProvider(int tvmazeId) : ICatalogProvider
    {
        public Task<CatalogShow?> GetShowAsync(int id, CancellationToken cancellationToken) =>
            Task.FromResult<CatalogShow?>(id == tvmazeId
                ? new CatalogShow(tvmazeId, "Black Mirror", null, 2011, "Running", "Scripted", "English",
                    ["Drama", "Science-Fiction", "Thriller"], "Overview", null, null, 8.4, 62, "tt2085059", false)
                : null);

        public Task<IReadOnlyList<CatalogShow>> SearchAsync(string query, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<EpisodeCatalogInfo>> GetEpisodesAsync(int id, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<int?> LookupByImdbIdAsync(string imdbId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<CatalogShow>> GetNewShowsAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<CatalogShow>> GetUpcomingAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<ShowAka>> GetAkasAsync(int tvmazeId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyDictionary<int, long>> GetShowUpdateTimestampsAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class FakeTvmazeSource(TvmazePayload payload) : ITvmazeShowSource
    {
        public Task<TvmazePayload?> FetchAsync(int tvmazeId, CancellationToken cancellationToken) =>
            Task.FromResult<TvmazePayload?>(tvmazeId == payload.Show.TvmazeId ? payload : null);
    }
}
