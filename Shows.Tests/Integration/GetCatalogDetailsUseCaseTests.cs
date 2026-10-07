using Shows.Application;
using Shows.Application.UseCases;
using Shows.Domain;
using Shows.Infrastructure.Persistence;
using Shows.Infrastructure.Persistence.Repositories;
using Shows.Infrastructure.Persistence.Sources;
using Xunit;

namespace Shows.Tests.Integration;

/// <summary>
/// Acceptance for <see cref="GetCatalogDetailsUseCase"/> — the read behind the untracked-details page,
/// now with episodes (user-issues 19 follow-up). The behaviour that matters: a Discovery show's <b>metadata</b>
/// is served from the cached snapshot with <b>no <c>GetShowAsync</c> call</b>, while its <b>episodes are
/// always fetched live</b> (the snapshot only holds scheduled rows, so the full guide needs the endpoint).
/// A Search show reads the show live too. Both then get the already-tracked marker from <c>user_show</c>
/// where <c>deleted_at IS NULL</c> (SPEC F2). Driven by a fake <see cref="ICatalogProvider"/> and a fake
/// <see cref="IDiscoverySource"/> over a migrated temp DB.
/// </summary>
public sealed class GetCatalogDetailsUseCaseTests : IClassFixture<DatabaseFixture>, IAsyncLifetime
{
    private const int CachedId = 700;
    private const int LiveId = 701;

    private readonly DatabaseFixture _fixture;

    public GetCatalogDetailsUseCaseTests(DatabaseFixture fixture) => _fixture = fixture;

    public Task InitializeAsync()
    {
        _fixture.Reset();
        return Task.CompletedTask;
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task A_cached_discovery_show_uses_cached_metadata_but_fetches_episodes_live()
    {
        // The Discovery fix: the show card comes from the cache (GetShowAsync must NOT be called — the
        // fake throws if it is), while the episode guide is always fetched live from the provider.
        var provider = new CachedMetadataProvider();
        var discovery = new CachedSource(CachedId, "Cached Show", episodes:
        [
            // These cached episodes must be ignored — the live fetch supplies the guide instead.
            new CatalogEpisode(1, 1, "Stale Scheduled Row", new DateOnly(2026, 9, 1), IsSpecial: false),
        ]);

        var details = await RunAsync(provider, discovery, CachedId);

        Assert.NotNull(details);
        Assert.Equal("Cached Show", details.Show.Title);
        // Episodes come from the live provider (the full guide), not the cached scheduled row, sorted by
        // air date descending — "Second" (2026-09-08) leads "Pilot" (2026-09-01).
        var season = Assert.Single(details.Seasons);
        Assert.Equal(1, season.SeasonNumber);
        Assert.Equal(["Second", "Pilot"], season.Episodes.Select(e => e.Title));
    }

    [Fact]
    public async Task A_show_absent_from_the_cache_falls_back_to_a_live_read()
    {
        // A Search hit is not in the snapshot, so the provider supplies the show and its episodes.
        var provider = new LiveProvider(LiveId);
        var discovery = new CachedSource(CachedId, "Unrelated", []);

        var details = await RunAsync(provider, discovery, LiveId);

        Assert.NotNull(details);
        Assert.Equal("Live Show", details.Show.Title);
        // Grouped descending by season: season 1 leads, specials (season 0) now trail.
        Assert.Equal([1, 0], details.Seasons.Select(s => s.SeasonNumber));
        Assert.True(details.Seasons[1].Episodes.Single().IsSpecial);
    }

    [Fact]
    public async Task The_already_tracked_marker_is_set_from_user_show()
    {
        await SeedTrackedShowAsync(CachedId, deleted: false);
        var discovery = new CachedSource(CachedId, "Cached Show", []);

        var details = await RunAsync(new CachedMetadataProvider(), discovery, CachedId);

        Assert.NotNull(details);
        Assert.True(details.Show.IsAlreadyTracked);
    }

    [Fact]
    public async Task A_live_show_the_provider_does_not_know_returns_null()
    {
        // Not in the cache, and the live provider has no such show (a stale hit) → null, not a crash.
        var details = await RunAsync(new LiveProvider(LiveId), new CachedSource(CachedId, "x", []), 999);

        Assert.Null(details);
    }

    // ---- helpers ----

    private async Task<CatalogDetails?> RunAsync(
        ICatalogProvider provider, IDiscoverySource discovery, int tvmazeId)
    {
        await using var context = _fixture.CreateContext();
        var useCase = new GetCatalogDetailsUseCase(provider, discovery, new TrackedShowRepository(context));
        return await useCase.ExecuteAsync(tvmazeId, CancellationToken.None);
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

    private static CatalogShow Show(int tvmazeId, string title) =>
        new(tvmazeId, title, null, null, null, null, null, [], null, null, null, null, null, null, false);

    /// <summary>
    /// A cached Discovery show: <see cref="GetShowAsync"/> must never be called (the metadata comes from
    /// the cache), but <see cref="GetEpisodesAsync"/> supplies the live episode guide.
    /// </summary>
    private sealed class CachedMetadataProvider : ICatalogProvider
    {
        public Task<CatalogShow?> GetShowAsync(int tvmazeId, CancellationToken ct) =>
            throw new InvalidOperationException("GetShowAsync must not be called for a cached show");

        public Task<IReadOnlyList<CatalogShow>> SearchAsync(string q, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<EpisodeCatalogInfo>> GetEpisodesAsync(int tvmazeId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<EpisodeCatalogInfo>>(
            [
                new EpisodeCatalogInfo(1, 1, 1, "Pilot", "regular", "2026-09-01", null, null, null, null, null, null),
                new EpisodeCatalogInfo(1, 1, 2, "Second", "regular", "2026-09-08", null, null, null, null, null, null),
            ]);

        public Task<int?> LookupByImdbIdAsync(string imdbId, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<ShowAka>> GetAkasAsync(int tvmazeId, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<IReadOnlyDictionary<int, long>> GetShowUpdateTimestampsAsync(CancellationToken ct) =>
            throw new NotSupportedException();
    }

    private sealed class LiveProvider(int liveId) : ICatalogProvider
    {
        public Task<CatalogShow?> GetShowAsync(int tvmazeId, CancellationToken ct) =>
            Task.FromResult<CatalogShow?>(tvmazeId == liveId ? Show(liveId, "Live Show") : null);

        public Task<IReadOnlyList<EpisodeCatalogInfo>> GetEpisodesAsync(int tvmazeId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<EpisodeCatalogInfo>>(
            [
                new EpisodeCatalogInfo(1, 1, 1, "Pilot", "regular", "2026-09-01", null, null, null, null, null, null),
                new EpisodeCatalogInfo(2, 0, null, "Holiday Special", "special", null, null, null, null, null, null, null),
            ]);

        public Task<IReadOnlyList<CatalogShow>> SearchAsync(string q, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<int?> LookupByImdbIdAsync(string imdbId, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<ShowAka>> GetAkasAsync(int tvmazeId, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<IReadOnlyDictionary<int, long>> GetShowUpdateTimestampsAsync(CancellationToken ct) =>
            throw new NotSupportedException();
    }

    private sealed class CachedSource(int tvmazeId, string title, IReadOnlyList<CatalogEpisode> episodes)
        : IDiscoverySource
    {
        public Task<DiscoveryPage> GetPageAsync(
            DiscoveryBucket bucket, DiscoveryFilter filter, int page, int pageSize, CancellationToken ct) =>
            throw new NotSupportedException();

        public bool TryGetCached(int id, out CatalogShow show, out IReadOnlyList<CatalogEpisode> eps)
        {
            if (id == tvmazeId)
            {
                show = Show(tvmazeId, title);
                eps = episodes;
                return true;
            }

            show = null!;
            eps = [];
            return false;
        }
    }
}
