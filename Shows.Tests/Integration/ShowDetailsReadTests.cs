using Microsoft.EntityFrameworkCore;
using Shows.Application;
using Shows.Application.UseCases;
using Shows.Domain;
using Shows.Infrastructure.Persistence;
using Shows.Infrastructure.Persistence.Repositories;
using Shows.Infrastructure.Persistence.Sources;
using Xunit;

namespace Shows.Tests.Integration;

/// <summary>
/// The two new Application/Infrastructure seams the details page (steps 5.2/6.1) depends on:
/// <see cref="SetSeasonExpandedUseCase"/> persists season expansion (SPEC R9), and
/// <see cref="ITrackedShowRepository.FindByIdWithGenresAsync"/> loads the genre names the details header
/// renders (SPEC F5). Both are exercised through the real repositories over a migrated temp DB.
/// </summary>
public sealed class ShowDetailsReadTests : IClassFixture<DatabaseFixture>, IAsyncLifetime
{
    private const int TvmazeId = 305;

    private readonly DatabaseFixture _fixture;
    private long _showId;

    public ShowDetailsReadTests(DatabaseFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync()
    {
        _fixture.Reset();
        _showId = 6_000_001;
        await SeedShowWithGenresAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task FindByIdWithGenres_loads_the_genre_names()
    {
        await using var context = _fixture.CreateContext();
        var repo = new TrackedShowRepository(context);

        var show = await repo.FindByIdWithGenresAsync(_showId, default);

        Assert.NotNull(show);
        Assert.Equal(2, show!.Genres.Count);
        Assert.Contains("Drama", show.Genres.Select(g => g.Name));
        Assert.Contains("Comedy", show.Genres.Select(g => g.Name));
    }

    // Regression for user-issues.md issue 2: the details page rendered an empty episode grid because
    // FindByIdWithGenresAsync omitted the Seasons include, so the VM (which groups episodes by iterating
    // TrackedShow.Seasons) had no seasons to build groups from even though episode rows existed. The
    // read model must return the populated header fields AND a non-empty Seasons navigation.
    [Fact]
    public async Task GetShowDetails_returns_populated_fields_for_a_tracked_show()
    {
        await using var context = _fixture.CreateContext();
        var useCase = new GetShowDetailsUseCase(
            new TrackedShowRepository(context),
            new EpisodeRepository(context),
            new UiStateRepository(context));

        var details = await useCase.ExecuteAsync(_showId, default);

        // Header fields the page renders.
        Assert.Equal("Seed Show", details.Show.Title);
        Assert.Equal("A seeded description.", details.Show.Description);
        Assert.Equal(2, details.Show.Genres.Count);
        // Network and air date reach the read model so the VM can render them (issue 2 coverage).
        Assert.Equal("NBC", details.Show.NetworkName);
        Assert.Equal(new DateOnly(2010, 3, 1), details.Show.PremiereDate);

        // The regression: the Seasons navigation must be loaded, not empty.
        Assert.NotEmpty(details.Show.Seasons);
        Assert.Single(details.Seasons);

        // Episodes arrive and group under the loaded season by SeasonId.
        Assert.Equal(2, details.Episodes.Count);
        var season = details.Seasons.Single();
        Assert.All(details.Episodes, e => Assert.Equal(season.Id, e.SeasonId));

        // The watch mark round-trips so the grid can render the toggle.
        Assert.Contains(7_000_001, details.WatchedEpisodeIds);
        Assert.DoesNotContain(7_000_002, details.WatchedEpisodeIds);
    }

    // Improvement 6 / ADR-034: the details read model must carry the EpisodeType flag so the UI can
    // partition insignificant specials out of the season grid into a bottom section — without the UI
    // reading episode_type from a source table (the flag lives on the Application read model's Episode
    // rows). Significant specials (EpisodeType.Special) stay in the grid and count toward progress.
    [Fact]
    public async Task GetShowDetails_carries_the_episode_type_flag_for_specials_grouping()
    {
        // Add a specials season (season 0) with one insignificant special to the seeded show.
        await using (var context = _fixture.CreateContext())
        {
            var specials = new Season { Id = 6_500_002, ShowId = _showId, SeasonNumber = 0 };
            context.Seasons.Add(specials);
            context.Episodes.Add(new Episode
            {
                Id = 7_000_003, ShowId = _showId, SeasonId = specials.Id,
                SeasonNumber = 0, EpisodeNumber = 1, Title = "A Special",
                EpisodeType = EpisodeType.InsignificantSpecial,
            });
            await context.SaveChangesAsync();
        }

        await using (var context = _fixture.CreateContext())
        {
            var useCase = new GetShowDetailsUseCase(
                new TrackedShowRepository(context),
                new EpisodeRepository(context),
                new UiStateRepository(context));

            var details = await useCase.ExecuteAsync(_showId, default);

            // The flag is present on the read model: exactly one insignificant special, the rest counted.
            // The UI partitions on this (EpisodeType.InsignificantSpecial) — it never reads a source table.
            Assert.Single(details.Episodes, e => e.EpisodeType == EpisodeType.InsignificantSpecial);
            Assert.Equal(2, details.Episodes.Count(e => e.EpisodeType != EpisodeType.InsignificantSpecial));
            // The special lives under the season-0 group, which the UI drops from the counted grid.
            Assert.Contains(details.Seasons, s => s.SeasonNumber == 0);
        }
    }

    [Fact]
    public async Task SetSeasonExpanded_persists_and_round_trips()
    {
        long seasonId = 6_500_001;

        await using (var context = _fixture.CreateContext())
        {
            var useCase = new SetSeasonExpandedUseCase(
                new UiStateRepository(context), new SequentialIdGenerator(), new UnitOfWork(context));
            await useCase.ExecuteAsync(_showId, seasonId, true, default);
        }

        await using (var context = _fixture.CreateContext())
        {
            var uiState = new UiStateRepository(context);
            var states = await uiState.ListByShowIdAsync(_showId, default);
            Assert.Single(states);
            Assert.True(states.Single(s => s.SeasonId == seasonId).IsExpanded);
        }

        // Toggle back: the same (show, season) row is updated, not duplicated (insert-or-update).
        await using (var context = _fixture.CreateContext())
        {
            var useCase = new SetSeasonExpandedUseCase(
                new UiStateRepository(context), new SequentialIdGenerator(), new UnitOfWork(context));
            await useCase.ExecuteAsync(_showId, seasonId, false, default);
        }

        await using (var context = _fixture.CreateContext())
        {
            var states = await new UiStateRepository(context).ListByShowIdAsync(_showId, default);
            Assert.Single(states);
            Assert.False(states.Single(s => s.SeasonId == seasonId).IsExpanded);
        }
    }

    // Regression for the "expand state is not saved" bug: ui_season_state.id is client-minted
    // (ValueGenerated.Never, ADR-027), but the insert path used to leave it at the default 0. The first
    // expanded season inserted fine; a SECOND season's insert also tried id 0 and threw a PK violation,
    // which the UI swallowed — so only ever one season could persist its expansion. One generator is
    // shared across both writes so the two rows get distinct ids.
    [Fact]
    public async Task SetSeasonExpanded_persists_multiple_seasons_without_id_collision()
    {
        var seasonA = 6_500_001;
        var seasonB = 6_500_002;
        var ids = new SequentialIdGenerator();

        await using (var context = _fixture.CreateContext())
        {
            // seasonA is seeded by InitializeAsync; seasonB exists only for this test (ui_season_state
            // has an FK to season, so the row must be present before its expansion state is written).
            context.Seasons.Add(new Season { Id = seasonB, ShowId = _showId, SeasonNumber = 2 });
            await context.SaveChangesAsync();
        }

        await using (var context = _fixture.CreateContext())
        {
            var useCase = new SetSeasonExpandedUseCase(
                new UiStateRepository(context), ids, new UnitOfWork(context));
            await useCase.ExecuteAsync(_showId, seasonA, true, default);
            await useCase.ExecuteAsync(_showId, seasonB, true, default);
        }

        await using (var context = _fixture.CreateContext())
        {
            var states = await new UiStateRepository(context).ListByShowIdAsync(_showId, default);
            Assert.Equal(2, states.Count);
            Assert.True(states.Single(s => s.SeasonId == seasonA).IsExpanded);
            Assert.True(states.Single(s => s.SeasonId == seasonB).IsExpanded);
        }
    }

    // Improvement 9 (details-page grouping): the UI partitions the mismatches by kind into a major group
    // (imdb_only / tvmaze_only) and a collapsed minor group (title / runtime / air_date_gap). The partition
    // itself is pure VM logic in the MAUI head (Shows.App), which this net8.0 test project cannot reference,
    // so it is verified by code path. What IS covered here is the precondition the VM relies on: the details
    // read model carries every mismatch with its kind intact, so the VM's Kind-based split sees the right
    // vocabulary. If a kind were dropped or renamed on the way through the read model, the grouping would
    // silently mis-bucket — this test is the guard against that.
    [Fact]
    public async Task GetShowDetails_carries_every_mismatch_kind_for_the_ui_grouping()
    {
        await using (var context = _fixture.CreateContext())
        {
            context.EpisodeMismatches.AddRange(
                new EpisodeMismatch
                {
                    Id = 9_000_001, ShowId = _showId, EpisodeId = null,
                    Kind = "imdb_only", ImdbValue = "An IMDb-only episode",
                    SeasonNumber = 1, EpisodeNumber = 3,
                },
                new EpisodeMismatch
                {
                    Id = 9_000_002, ShowId = _showId, EpisodeId = 7_000_001,
                    Kind = "tvmaze_only", TvmazeValue = "Pilot",
                    SeasonNumber = 1, EpisodeNumber = 1,
                },
                new EpisodeMismatch
                {
                    Id = 9_000_003, ShowId = _showId, EpisodeId = 7_000_002,
                    Kind = "title", TvmazeValue = "Second", ImdbValue = "The Second",
                    SeasonNumber = 1, EpisodeNumber = 2,
                },
                new EpisodeMismatch
                {
                    Id = 9_000_004, ShowId = _showId, EpisodeId = 7_000_002,
                    Kind = "runtime", TvmazeValue = "45", ImdbValue = "50",
                    SeasonNumber = 1, EpisodeNumber = 2,
                },
                new EpisodeMismatch
                {
                    Id = 9_000_005, ShowId = _showId, EpisodeId = 7_000_002,
                    Kind = "air_date_gap", TvmazeValue = null, ImdbValue = "2010",
                    SeasonNumber = 1, EpisodeNumber = 2,
                });
            await context.SaveChangesAsync();
        }

        await using (var context = _fixture.CreateContext())
        {
            var useCase = new GetShowDetailsUseCase(
                new TrackedShowRepository(context),
                new EpisodeRepository(context),
                new UiStateRepository(context));

            var details = await useCase.ExecuteAsync(_showId, default);

            var kinds = details.Mismatches.Select(m => m.Kind).ToList();

            // Major group (imdb_only / tvmaze_only) — the episode-set differences the VM always shows.
            Assert.Contains("imdb_only", kinds);
            Assert.Contains("tvmaze_only", kinds);
            // Minor group (title / runtime / air_date_gap) — the collapsed field-level differences.
            Assert.Contains("title", kinds);
            Assert.Contains("runtime", kinds);
            Assert.Contains("air_date_gap", kinds);

            // The exact partition the VM computes: major = {imdb_only, tvmaze_only}, minor = the rest.
            var major = details.Mismatches.Where(m => m.Kind is "imdb_only" or "tvmaze_only").ToList();
            var minor = details.Mismatches.Where(m => m.Kind is not ("imdb_only" or "tvmaze_only")).ToList();
            Assert.Equal(2, major.Count);
            Assert.Equal(3, minor.Count);
            Assert.All(minor, m => Assert.DoesNotContain(m.Kind, new[] { "imdb_only", "tvmaze_only" }));

            // The denormalised numbers survive so an imdb_only row (no episode) still labels "S01E03".
            var imdbOnly = details.Mismatches.Single(m => m.Kind == "imdb_only");
            Assert.Null(imdbOnly.EpisodeId);
            Assert.Equal(1, imdbOnly.SeasonNumber);
            Assert.Equal(3, imdbOnly.EpisodeNumber);
        }
    }

    // ---- helpers ----

    private async Task SeedShowWithGenresAsync()
    {
        await using var context = _fixture.CreateContext();

        context.TvmazeShows.Add(new TvmazeShow
        {
            TvmazeId = TvmazeId,
            Name = "Seed",
            StatusRaw = "Ended",
            RawJson = "json",
            FetchedAt = DateTimeOffset.UtcNow,
        });

        var drama = new Genre { Id = 1, Name = "Drama" };
        var comedy = new Genre { Id = 2, Name = "Comedy" };
        context.Genres.AddRange(drama, comedy);

        var show = new TrackedShow
        {
            Id = _showId,
            TvmazeId = TvmazeId,
            Title = "Seed Show",
            Description = "A seeded description.",
            NetworkName = "NBC",
            PremiereDate = new DateOnly(2010, 3, 1),
            ScheduleDays = "Thursday",
        };
        context.Entry(show).Property("tvmaze_show_id").CurrentValue = TvmazeId;
        show.Genres.Add(drama);
        show.Genres.Add(comedy);
        show.User = new UserShow
        {
            TrackedShowId = _showId,
            Category = ShowCategory.Started,
            AddedAt = DateTimeOffset.UtcNow,
            CategoryChangedAt = DateTimeOffset.UtcNow,
        };
        context.TrackedShows.Add(show);

        var season = new Season { Id = 6_500_001, ShowId = _showId, SeasonNumber = 1 };
        context.Seasons.Add(season);
        context.Episodes.AddRange(
            new Episode
            {
                Id = 7_000_001, ShowId = _showId, SeasonId = season.Id,
                SeasonNumber = 1, EpisodeNumber = 1, Title = "Pilot",
                EpisodeType = EpisodeType.Regular,
            },
            new Episode
            {
                Id = 7_000_002, ShowId = _showId, SeasonId = season.Id,
                SeasonNumber = 1, EpisodeNumber = 2, Title = "Second",
                EpisodeType = EpisodeType.Regular,
            });
        context.EpisodeWatches.Add(new EpisodeWatch
        {
            Id = 8_000_001, ShowId = _showId, EpisodeId = 7_000_001,
            WatchedAt = DateTimeOffset.UtcNow,
        });
        await context.SaveChangesAsync();
    }

    // Distinct positive ids so two ui_season_state rows never collide on the client-minted PK.
    private sealed class SequentialIdGenerator : IIdGenerator
    {
        private long _next = 9_000_000;
        public long NextId() => _next++;
    }
}
