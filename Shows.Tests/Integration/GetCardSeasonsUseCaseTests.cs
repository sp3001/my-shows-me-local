using Shows.Application.UseCases;
using Shows.Domain;
using Shows.Infrastructure.Persistence;
using Shows.Infrastructure.Persistence.Repositories;
using Shows.Infrastructure.Persistence.Sources;

namespace Shows.Tests.Integration;

/// <summary>
/// The light card read (issue 7 / improvement 4): <see cref="GetCardSeasonsUseCase"/> returns the season
/// list + persisted expansion (R9) and, for the EXPANDED seasons only, the filtered inline rows
/// (unwatched, already-aired regulars). Collapsed seasons carry no rows at load time — they load lazily via
/// <see cref="GetCardSeasonsUseCase.ExecuteSeasonAsync"/>. The details page is a different read model and is
/// unaffected (improvement 11).
/// </summary>
public sealed class GetCardSeasonsUseCaseTests : IClassFixture<DatabaseFixture>, IAsyncLifetime
{
    private const int TvmazeId = 411;
    private static readonly DateOnly Today = new(2026, 9, 30);

    private readonly DatabaseFixture _fixture;
    private long _showId;
    private long _season1;
    private long _season2;

    public GetCardSeasonsUseCaseTests(DatabaseFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync()
    {
        _fixture.Reset();
        _showId = 6_100_001;
        _season1 = 6_150_001;
        _season2 = 6_150_002;
        await SeedAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    // Season 2 is persisted-expanded; season 1 is collapsed. The expanded season's rows are filtered
    // (aired+unwatched regulars only); the collapsed season contributes no rows at load time.
    [Fact]
    public async Task ExecuteAsync_returns_expanded_rows_only_and_marks_expansion()
    {
        await using var context = _fixture.CreateContext();
        var useCase = UseCase(context);

        var card = await useCase.ExecuteAsync(_showId, Today, default);

        // Both seasons present, in number order.
        Assert.Equal(new[] { 1, 2 }, card.Seasons.Select(s => s.SeasonNumber));

        // Only season 2 is expanded (season 1 has no ui_season_state row → collapsed).
        Assert.Contains(_season2, card.ExpandedSeasonIds);
        Assert.DoesNotContain(_season1, card.ExpandedSeasonIds);

        // Rows exist for the expanded season only.
        Assert.True(card.EpisodesByExpandedSeason.ContainsKey(_season2));
        Assert.False(card.EpisodesByExpandedSeason.ContainsKey(_season1));

        // Season 2 has 3 episodes: e1 aired+unwatched (kept), e2 watched (dropped), e3 future (dropped).
        var s2 = card.EpisodesByExpandedSeason[_season2];
        Assert.Single(s2);
        Assert.Equal(7_100_004, s2.Single().Id);
    }

    // The lazy path: a collapsed season's filtered rows materialize only when asked for.
    [Fact]
    public async Task ExecuteSeasonAsync_returns_filtered_rows_for_a_collapsed_season()
    {
        await using var context = _fixture.CreateContext();
        var useCase = UseCase(context);

        // Season 1: e1 aired+unwatched (kept), e2 watched (dropped), e3 no air date (dropped).
        var rows = await useCase.ExecuteSeasonAsync(_showId, _season1, Today, default);

        Assert.Single(rows);
        Assert.Equal(7_100_001, rows.Single().Id);
    }

    // Ordering within a season: newest air date first (improvement 11's comparator, reused on the card).
    [Fact]
    public async Task Expanded_season_rows_are_ordered_newest_first()
    {
        // Add a second aired+unwatched episode to season 2 with a later air date.
        await using (var context = _fixture.CreateContext())
        {
            context.Episodes.Add(new Episode
            {
                Id = 7_100_006, ShowId = _showId, SeasonId = _season2,
                SeasonNumber = 2, EpisodeNumber = 4, Title = "Later",
                EpisodeType = EpisodeType.Regular, AirDate = new DateOnly(2026, 9, 28),
            });
            await context.SaveChangesAsync();
        }

        await using (var context = _fixture.CreateContext())
        {
            var card = await UseCase(context).ExecuteAsync(_showId, Today, default);
            var ids = card.EpisodesByExpandedSeason[_season2].Select(e => e.Id).ToList();
            Assert.Equal(new long[] { 7_100_006, 7_100_004 }, ids);
        }
    }

    private GetCardSeasonsUseCase UseCase(ShowsDbContext context) =>
        new(new TrackedShowRepository(context), new EpisodeRepository(context), new UiStateRepository(context));

    private async Task SeedAsync()
    {
        await using var context = _fixture.CreateContext();

        context.TvmazeShows.Add(new TvmazeShow
        {
            TvmazeId = TvmazeId,
            Name = "Card Seed",
            StatusRaw = "Running",
            RawJson = "json",
            FetchedAt = DateTimeOffset.UtcNow,
        });

        var show = new TrackedShow
        {
            Id = _showId,
            TvmazeId = TvmazeId,
            Title = "Card Seed Show",
        };
        context.Entry(show).Property("tvmaze_show_id").CurrentValue = TvmazeId;
        show.User = new UserShow
        {
            TrackedShowId = _showId,
            Category = ShowCategory.Started,
            AddedAt = DateTimeOffset.UtcNow,
            CategoryChangedAt = DateTimeOffset.UtcNow,
        };
        context.TrackedShows.Add(show);

        var s1 = new Season { Id = _season1, ShowId = _showId, SeasonNumber = 1 };
        var s2 = new Season { Id = _season2, ShowId = _showId, SeasonNumber = 2 };
        context.Seasons.AddRange(s1, s2);

        context.Episodes.AddRange(
            // Season 1 (collapsed): aired+unwatched, watched, no-air-date.
            new Episode { Id = 7_100_001, ShowId = _showId, SeasonId = _season1, SeasonNumber = 1, EpisodeNumber = 1, Title = "S1E1", EpisodeType = EpisodeType.Regular, AirDate = new DateOnly(2025, 1, 1) },
            new Episode { Id = 7_100_002, ShowId = _showId, SeasonId = _season1, SeasonNumber = 1, EpisodeNumber = 2, Title = "S1E2", EpisodeType = EpisodeType.Regular, AirDate = new DateOnly(2025, 1, 8) },
            new Episode { Id = 7_100_003, ShowId = _showId, SeasonId = _season1, SeasonNumber = 1, EpisodeNumber = 3, Title = "S1E3", EpisodeType = EpisodeType.Regular, AirDate = null },
            // Season 2 (expanded): aired+unwatched, watched, future.
            new Episode { Id = 7_100_004, ShowId = _showId, SeasonId = _season2, SeasonNumber = 2, EpisodeNumber = 1, Title = "S2E1", EpisodeType = EpisodeType.Regular, AirDate = new DateOnly(2026, 9, 21) },
            new Episode { Id = 7_100_005, ShowId = _showId, SeasonId = _season2, SeasonNumber = 2, EpisodeNumber = 2, Title = "S2E2", EpisodeType = EpisodeType.Regular, AirDate = new DateOnly(2026, 9, 14) },
            new Episode { Id = 7_100_007, ShowId = _showId, SeasonId = _season2, SeasonNumber = 2, EpisodeNumber = 3, Title = "S2E3", EpisodeType = EpisodeType.Regular, AirDate = new DateOnly(2026, 12, 25) });

        context.EpisodeWatches.AddRange(
            new EpisodeWatch { Id = 8_100_001, ShowId = _showId, EpisodeId = 7_100_002, WatchedAt = DateTimeOffset.UtcNow },
            new EpisodeWatch { Id = 8_100_002, ShowId = _showId, EpisodeId = 7_100_005, WatchedAt = DateTimeOffset.UtcNow });

        // Persist season 2 as expanded (R9); season 1 stays collapsed (no row).
        context.UiSeasonStates.Add(new UiSeasonState { Id = 9_100_001, ShowId = _showId, SeasonId = _season2, IsExpanded = true });

        await context.SaveChangesAsync();
    }
}
