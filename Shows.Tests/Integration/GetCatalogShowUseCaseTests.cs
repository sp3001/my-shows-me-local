using Shows.Application;
using Shows.Application.UseCases;
using Shows.Domain;
using Shows.Infrastructure.Persistence;
using Shows.Infrastructure.Persistence.Repositories;
using Shows.Infrastructure.Persistence.Sources;
using Xunit;

namespace Shows.Tests.Integration;

/// <summary>
/// Acceptance for <see cref="GetCatalogShowUseCase"/> — the read behind the search-details page
/// (user-issues 3 / improvement 10). A search hit is not persisted (SPEC P2/R8a), so the details view
/// for an untracked show reads the catalog record by TVmaze id and marks whether the user already tracks
/// it. The assertion that matters is the same one as the search marker: it reads <c>user_show</c> where
/// <c>deleted_at IS NULL</c>, <b>not</b> <c>tracked_show</c> (SPEC F2) — a soft-deleted show must report
/// untracked so the page still offers add. Driven by a fake <see cref="ICatalogProvider"/> and the real
/// repository over a migrated temp DB — no network.
/// </summary>
public sealed class GetCatalogShowUseCaseTests : IClassFixture<DatabaseFixture>, IAsyncLifetime
{
    private const int TvmazeId = 305;

    private readonly DatabaseFixture _fixture;

    public GetCatalogShowUseCaseTests(DatabaseFixture fixture) => _fixture = fixture;

    public Task InitializeAsync()
    {
        _fixture.Reset();
        return Task.CompletedTask;
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task An_untracked_show_is_returned_with_the_marker_false()
    {
        var show = await GetAsync();

        Assert.NotNull(show);
        Assert.Equal(TvmazeId, show.TvmazeId);
        Assert.False(show.IsAlreadyTracked);
    }

    [Fact]
    public async Task An_actively_tracked_show_is_returned_with_the_marker_true()
    {
        await SeedTrackedShowAsync(deleted: false);

        var show = await GetAsync();

        Assert.NotNull(show);
        Assert.True(show.IsAlreadyTracked);
    }

    [Fact]
    public async Task A_soft_deleted_show_is_reported_as_not_tracked()
    {
        // SPEC F2 / ADR-024: the tracked_show row survives an untrack, but the marker reads the
        // user_show.deleted_at flag, so the details page still offers the add.
        await SeedTrackedShowAsync(deleted: true);

        var show = await GetAsync();

        Assert.NotNull(show);
        Assert.False(show.IsAlreadyTracked);
    }

    [Fact]
    public async Task A_show_the_provider_does_not_know_returns_null()
    {
        // A stale search hit: the provider has no record, so the page shows a friendly "not available"
        // rather than crashing. Nothing is written.
        await using var context = _fixture.CreateContext();
        var useCase = new GetCatalogShowUseCase(new EmptyProvider(), new TrackedShowRepository(context));

        var show = await useCase.ExecuteAsync(TvmazeId, CancellationToken.None);
        Assert.Null(show);
    }

    // ---- helpers ----

    private async Task<CatalogShow?> GetAsync()
    {
        await using var context = _fixture.CreateContext();
        var useCase = new GetCatalogShowUseCase(new OneShowProvider(), new TrackedShowRepository(context));
        return await useCase.ExecuteAsync(TvmazeId, CancellationToken.None);
    }

    private async Task SeedTrackedShowAsync(bool deleted)
    {
        await using var context = _fixture.CreateContext();
        context.TvmazeShows.Add(new TvmazeShow
        {
            TvmazeId = TvmazeId,
            Name = "Seed",
            StatusRaw = "Running",
            RawJson = "json",
            FetchedAt = DateTimeOffset.UtcNow,
        });

        var showId = 5_000_000 + TvmazeId;
        var show = new TrackedShow { Id = showId, TvmazeId = TvmazeId, Title = "Seed" };
        context.Entry(show).Property("tvmaze_show_id").CurrentValue = TvmazeId;
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

    private sealed class OneShowProvider : ICatalogProvider
    {
        public Task<CatalogShow?> GetShowAsync(int tvmazeId, CancellationToken cancellationToken) =>
            Task.FromResult<CatalogShow?>(tvmazeId == TvmazeId
                ? new CatalogShow(TvmazeId, "Black Mirror", null, 2011, "Running", "Scripted", "English",
                    ["Drama"], "Overview", null, null, 8.4, 62, null, false)
                : null);

        public Task<IReadOnlyList<CatalogShow>> SearchAsync(string query, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<EpisodeCatalogInfo>> GetEpisodesAsync(int tvmazeId, CancellationToken cancellationToken) =>
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

    private sealed class EmptyProvider : ICatalogProvider
    {
        public Task<CatalogShow?> GetShowAsync(int tvmazeId, CancellationToken cancellationToken) =>
            Task.FromResult<CatalogShow?>(null);

        public Task<IReadOnlyList<CatalogShow>> SearchAsync(string query, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<EpisodeCatalogInfo>> GetEpisodesAsync(int tvmazeId, CancellationToken cancellationToken) =>
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
}
