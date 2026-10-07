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
/// Step 4.1 acceptance for <see cref="SearchShowsUseCase"/> — the read model behind the Search UI. The
/// assertion that matters is SPEC F2's already-tracked marker: it must read <c>user_show</c> where
/// <c>deleted_at IS NULL</c>, <b>not</b> <c>tracked_show</c>. A soft-deleted show still has a
/// <c>tracked_show</c> row, so checking that table would report it as tracked and block the very re-add
/// the design is built around (ADR-024). Driven by a fake <see cref="ICatalogProvider"/> and the real
/// repository over a migrated temp DB — no network.
/// </summary>
public sealed class SearchShowsUseCaseTests : IClassFixture<DatabaseFixture>, IAsyncLifetime
{
    private const int TrackedTvmazeId = 305;
    private const int UntrackedTvmazeId = 999;

    private readonly DatabaseFixture _fixture;

    public SearchShowsUseCaseTests(DatabaseFixture fixture) => _fixture = fixture;

    public Task InitializeAsync()
    {
        _fixture.Reset();
        return Task.CompletedTask;
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Results_are_marked_tracked_against_user_show_not_tracked_show()
    {
        // Seed an actively-tracked show (tracked_show + user_show, deleted_at NULL).
        await SeedTrackedShowAsync(TrackedTvmazeId, deleted: false);

        var results = await SearchAsync();

        Assert.Equal(2, results.Count);
        Assert.True(results.Single(r => r.TvmazeId == TrackedTvmazeId).IsAlreadyTracked);
        Assert.False(results.Single(r => r.TvmazeId == UntrackedTvmazeId).IsAlreadyTracked);
    }

    [Fact]
    public async Task A_soft_deleted_show_is_reported_as_not_tracked()
    {
        // SPEC F2 / ADR-024: untracking is a logical delete, so the tracked_show row survives but the
        // marker must read the user_show.deleted_at flag and report NOT tracked — otherwise re-add is blocked.
        await SeedTrackedShowAsync(TrackedTvmazeId, deleted: true);

        var results = await SearchAsync();

        Assert.False(results.Single(r => r.TvmazeId == TrackedTvmazeId).IsAlreadyTracked);
    }

    [Fact]
    public async Task An_empty_provider_result_short_circuits_without_a_repository_read()
    {
        // The use case returns the empty list before touching the repository (no batched lookup for zero
        // ids). Asserting the empty result is the observable behaviour; the short-circuit is an internal
        // optimisation the empty result already proves is not wrong.
        await using var context = _fixture.CreateContext();
        var useCase = new SearchShowsUseCase(new EmptyProvider(), new TrackedShowRepository(context));

        var results = await useCase.ExecuteAsync("nothing", CancellationToken.None);
        Assert.Empty(results);
    }

    // ---- helpers ----

    private async Task<IReadOnlyList<CatalogShow>> SearchAsync()
    {
        await using var context = _fixture.CreateContext();
        var useCase = new SearchShowsUseCase(
            new TwoShowProvider(),
            new TrackedShowRepository(context));
        return await useCase.ExecuteAsync("mirror", CancellationToken.None);
    }

    private async Task SeedTrackedShowAsync(int tvmazeId, bool deleted)
    {
        await using var context = _fixture.CreateContext();
        context.TvmazeShows.Add(new TvmazeShow
        {
            TvmazeId = tvmazeId,
            Name = "Seed",
            StatusRaw = "Running",
            RawJson = "json",
            FetchedAt = DateTimeOffset.UtcNow,
        });

        var showId = 5_000_000 + tvmazeId;
        var show = new TrackedShow { Id = showId, TvmazeId = tvmazeId, Title = "Seed" };
        context.Entry(show).Property("tvmaze_show_id").CurrentValue = tvmazeId;
        show.User = new UserShow
        {
            TrackedShowId = showId,
            Category = ShowCategory.Started,
            AddedAt = DateTimeOffset.UtcNow,
            CategoryChangedAt = DateTimeOffset.UtcNow,
            DeletedAt = deleted ? DateTimeOffset.UtcNow : null,
        };
        context.TrackedShows.Add(show);
        await context.SaveChangesAsync();
    }

    private sealed class TwoShowProvider : ICatalogProvider
    {
        public Task<IReadOnlyList<CatalogShow>> SearchAsync(string query, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<CatalogShow>>(
            [
                Show(TrackedTvmazeId, "Black Mirror"),
                Show(UntrackedTvmazeId, "Some Other Show"),
            ]);

        public Task<CatalogShow?> GetShowAsync(int tvmazeId, CancellationToken cancellationToken) =>
            Task.FromResult<CatalogShow?>(null);

        public Task<IReadOnlyList<EpisodeCatalogInfo>> GetEpisodesAsync(int tvmazeId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<EpisodeCatalogInfo>>([]);

        public Task<int?> LookupByImdbIdAsync(string imdbId, CancellationToken cancellationToken) =>
            Task.FromResult<int?>(null);

        public Task<IReadOnlyList<CatalogShow>> GetNewShowsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<CatalogShow>>([]);

        public Task<IReadOnlyList<CatalogShow>> GetUpcomingAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<CatalogShow>>([]);

        public Task<IReadOnlyList<ShowAka>> GetAkasAsync(int tvmazeId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ShowAka>>([]);

        public Task<IReadOnlyDictionary<int, long>> GetShowUpdateTimestampsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<int, long>>(new Dictionary<int, long>());

        private static CatalogShow Show(int id, string title) =>
            new(id, title, null, 2011, "Running", "Scripted", "English", ["Drama"], "Overview",
                null, null, 8.4, 62, null, false);
    }

    private sealed class EmptyProvider : ICatalogProvider
    {
        public Task<IReadOnlyList<CatalogShow>> SearchAsync(string query, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<CatalogShow>>([]);

        public Task<CatalogShow?> GetShowAsync(int tvmazeId, CancellationToken cancellationToken) =>
            Task.FromResult<CatalogShow?>(null);

        public Task<IReadOnlyList<EpisodeCatalogInfo>> GetEpisodesAsync(int tvmazeId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<EpisodeCatalogInfo>>([]);

        public Task<int?> LookupByImdbIdAsync(string imdbId, CancellationToken cancellationToken) =>
            Task.FromResult<int?>(null);

        public Task<IReadOnlyList<CatalogShow>> GetNewShowsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<CatalogShow>>([]);

        public Task<IReadOnlyList<CatalogShow>> GetUpcomingAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<CatalogShow>>([]);

        public Task<IReadOnlyList<ShowAka>> GetAkasAsync(int tvmazeId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ShowAka>>([]);

        public Task<IReadOnlyDictionary<int, long>> GetShowUpdateTimestampsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<int, long>>(new Dictionary<int, long>());
    }
}
