using Shows.Application.UseCases;
using Shows.Domain;
using Shows.Infrastructure.Persistence;
using Shows.Infrastructure.Persistence.Repositories;
using Shows.Infrastructure.Persistence.Sources;

namespace Shows.Tests.Integration;

/// <summary>
/// The batched Started-list read (<see cref="GetStartedCardsUseCase"/>): the same per-show data the
/// single-card read returns, but loaded for many shows in one pass. The contract that matters is that the
/// batched slice for a show is <b>identical</b> to what <see cref="GetCardSeasonsUseCase.ExecuteAsync"/>
/// returns for that show alone — the card builds from either interchangeably.
/// </summary>
public sealed class GetStartedCardsUseCaseTests : IClassFixture<DatabaseFixture>, IAsyncLifetime
{
    private static readonly DateOnly Today = new(2026, 9, 30);

    private readonly DatabaseFixture _fixture;

    // Show A: season 1 collapsed (one aired+unwatched), season 2 expanded (aired+unwatched kept, watched
    // dropped, future dropped). Show B: one collapsed season with a single aired+unwatched episode.
    private long _showA;
    private long _showB;
    private long _aSeason1;
    private long _aSeason2;
    private long _bSeason1;

    public GetStartedCardsUseCaseTests(DatabaseFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync()
    {
        _fixture.Reset();
        _showA = 6_200_001;
        _showB = 6_200_002;
        _aSeason1 = 6_250_001;
        _aSeason2 = 6_250_002;
        _bSeason1 = 6_250_003;
        await SeedAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    // The batched slice for each show must match the single-card read exactly.
    [Fact]
    public async Task Batched_slice_matches_single_card_read_per_show()
    {
        await using var context = _fixture.CreateContext();
        var batch = Batch(context);
        var single = Single(context);

        var batchResult = await batch.ExecuteAsync([_showA, _showB], Today, default);
        var byShow = batchResult.ByShow;

        Assert.Equal(2, byShow.Count);

        var a = byShow[_showA];
        var aSingle = await single.ExecuteAsync(_showA, Today, default);

        Assert.Equal(aSingle.Seasons.Select(s => s.SeasonNumber), a.Seasons.Select(s => s.SeasonNumber));
        Assert.Equal(aSingle.ExpandedSeasonIds, a.ExpandedSeasonIds);
        Assert.Equal(
            aSingle.EpisodesByExpandedSeason.ToDictionary(k => k.Key, v => v.Value.Select(e => e.Id).ToList()),
            a.EpisodesByExpandedSeason.ToDictionary(k => k.Key, v => v.Value.Select(e => e.Id).ToList()));
        Assert.Equal(aSingle.DisplayableCountBySeason, a.DisplayableCountBySeason);

        var b = byShow[_showB];
        var bSingle = await single.ExecuteAsync(_showB, Today, default);
        Assert.Equal(bSingle.DisplayableCountBySeason, b.DisplayableCountBySeason);
        Assert.Empty(b.ExpandedSeasonIds);
    }

    // The batched count must exclude watched, future, and special episodes — the same predicate as the
    // single-show aggregate — across every show in one grouped query.
    [Fact]
    public async Task Batched_count_excludes_watched_future_and_specials()
    {
        await using var context = _fixture.CreateContext();
        var byShow = (await Batch(context).ExecuteAsync([_showA, _showB], Today, default)).ByShow;

        // Show A season 1: one aired+unwatched regular → count 1.
        Assert.Equal(1, byShow[_showA].DisplayableCountBySeason[_aSeason1]);
        // Show A season 2: aired+unwatched kept, watched + future dropped → count 1.
        Assert.Equal(1, byShow[_showA].DisplayableCountBySeason[_aSeason2]);
        // Show B season 1: one aired+unwatched regular → count 1.
        Assert.Equal(1, byShow[_showB].DisplayableCountBySeason[_bSeason1]);
    }

    // A show with no seasons still gets an entry (empty), so the caller never special-cases a miss.
    [Fact]
    public async Task ExecuteAsync_returns_empty_entry_for_a_show_with_no_seasons()
    {
        await using var context = _fixture.CreateContext();
        var byShow = (await Batch(context).ExecuteAsync([_showA, 9_999_999], Today, default)).ByShow;

        Assert.True(byShow.ContainsKey(9_999_999));
        Assert.Empty(byShow[9_999_999].Seasons);
    }

    // The batched read carries each show's most recent already-aired date for the Started sort
    // (improvement 12). Show A's latest aired episode is 2026-09-21 (the 2026-12-25 one is future, excluded);
    // Show B's is 2025-03-03. A show with no aired episode is absent.
    [Fact]
    public async Task Batched_read_carries_last_aired_date_per_show()
    {
        await using var context = _fixture.CreateContext();
        var batch = await Batch(context).ExecuteAsync([_showA, _showB, 9_999_999], Today, default);

        Assert.Equal(new DateOnly(2026, 9, 21), batch.LastAiredByShow[_showA]);
        Assert.Equal(new DateOnly(2025, 3, 3), batch.LastAiredByShow[_showB]);
        Assert.False(batch.LastAiredByShow.ContainsKey(9_999_999));
    }

    private static GetStartedCardsUseCase Batch(ShowsDbContext context) =>
        new(new TrackedShowRepository(context), new EpisodeRepository(context), new UiStateRepository(context));

    private static GetCardSeasonsUseCase Single(ShowsDbContext context) =>
        new(new TrackedShowRepository(context), new EpisodeRepository(context), new UiStateRepository(context));

    private async Task SeedAsync()
    {
        await using var context = _fixture.CreateContext();

        context.TvmazeShows.AddRange(
            new TvmazeShow { TvmazeId = 501, Name = "A", StatusRaw = "Running", RawJson = "j", FetchedAt = DateTimeOffset.UtcNow },
            new TvmazeShow { TvmazeId = 502, Name = "B", StatusRaw = "Running", RawJson = "j", FetchedAt = DateTimeOffset.UtcNow });

        AddShow(context, _showA, 501, "Show A");
        AddShow(context, _showB, 502, "Show B");

        context.Seasons.AddRange(
            new Season { Id = _aSeason1, ShowId = _showA, SeasonNumber = 1 },
            new Season { Id = _aSeason2, ShowId = _showA, SeasonNumber = 2 },
            new Season { Id = _bSeason1, ShowId = _showB, SeasonNumber = 1 });

        context.Episodes.AddRange(
            // Show A season 1 (collapsed): aired+unwatched.
            new Episode { Id = 7_200_001, ShowId = _showA, SeasonId = _aSeason1, SeasonNumber = 1, EpisodeNumber = 1, Title = "A1E1", EpisodeType = EpisodeType.Regular, AirDate = new DateOnly(2025, 1, 1) },
            // Show A season 2 (expanded): aired+unwatched, watched, future.
            new Episode { Id = 7_200_002, ShowId = _showA, SeasonId = _aSeason2, SeasonNumber = 2, EpisodeNumber = 1, Title = "A2E1", EpisodeType = EpisodeType.Regular, AirDate = new DateOnly(2026, 9, 21) },
            new Episode { Id = 7_200_003, ShowId = _showA, SeasonId = _aSeason2, SeasonNumber = 2, EpisodeNumber = 2, Title = "A2E2", EpisodeType = EpisodeType.Regular, AirDate = new DateOnly(2026, 9, 14) },
            new Episode { Id = 7_200_004, ShowId = _showA, SeasonId = _aSeason2, SeasonNumber = 2, EpisodeNumber = 3, Title = "A2E3", EpisodeType = EpisodeType.Regular, AirDate = new DateOnly(2026, 12, 25) },
            // Show B season 1 (collapsed): aired+unwatched.
            new Episode { Id = 7_200_005, ShowId = _showB, SeasonId = _bSeason1, SeasonNumber = 1, EpisodeNumber = 1, Title = "B1E1", EpisodeType = EpisodeType.Regular, AirDate = new DateOnly(2025, 3, 3) });

        context.EpisodeWatches.Add(
            new EpisodeWatch { Id = 8_200_001, ShowId = _showA, EpisodeId = 7_200_003, WatchedAt = DateTimeOffset.UtcNow });

        // Show A season 2 persisted-expanded; everything else collapsed.
        context.UiSeasonStates.Add(new UiSeasonState { Id = 9_200_001, ShowId = _showA, SeasonId = _aSeason2, IsExpanded = true });

        await context.SaveChangesAsync();
    }

    private static void AddShow(ShowsDbContext context, long id, int tvmazeId, string title)
    {
        var show = new TrackedShow { Id = id, TvmazeId = tvmazeId, Title = title };
        context.Entry(show).Property("tvmaze_show_id").CurrentValue = tvmazeId;
        show.User = new UserShow
        {
            TrackedShowId = id,
            Category = ShowCategory.Started,
            AddedAt = DateTimeOffset.UtcNow,
            CategoryChangedAt = DateTimeOffset.UtcNow,
        };
        context.TrackedShows.Add(show);
    }
}
