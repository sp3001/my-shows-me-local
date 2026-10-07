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
/// User-issues 19 acceptance for <see cref="GetDiscoveryPageUseCase"/>: the page the source returns is
/// marked against <c>user_show</c> where <c>deleted_at IS NULL</c> — the same SPEC F2 rule Search enforces.
/// A soft-deleted show must report untracked so re-add is not blocked (ADR-024). Driven by a fake
/// <see cref="IDiscoverySource"/> and the real repository over a migrated temp DB — no network.
/// </summary>
public sealed class GetDiscoveryPageUseCaseTests : IClassFixture<DatabaseFixture>, IAsyncLifetime
{
    private const int TrackedTvmazeId = 305;
    private const int UntrackedTvmazeId = 999;

    private readonly DatabaseFixture _fixture;

    public GetDiscoveryPageUseCaseTests(DatabaseFixture fixture) => _fixture = fixture;

    public Task InitializeAsync()
    {
        _fixture.Reset();
        return Task.CompletedTask;
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Page_items_are_marked_tracked_against_user_show_not_tracked_show()
    {
        await SeedTrackedShowAsync(TrackedTvmazeId, deleted: false);

        var page = await QueryAsync();

        Assert.Equal(2, page.Items.Count);
        Assert.True(page.Items.Single(r => r.TvmazeId == TrackedTvmazeId).IsAlreadyTracked);
        Assert.False(page.Items.Single(r => r.TvmazeId == UntrackedTvmazeId).IsAlreadyTracked);
    }

    [Fact]
    public async Task A_soft_deleted_show_is_reported_as_not_tracked()
    {
        await SeedTrackedShowAsync(TrackedTvmazeId, deleted: true);

        var page = await QueryAsync();

        Assert.False(page.Items.Single(r => r.TvmazeId == TrackedTvmazeId).IsAlreadyTracked);
    }

    [Fact]
    public async Task Paging_metadata_survives_the_tracked_marker_pass()
    {
        // The use case rebuilds the page with marked items; it must carry the source's page/total through.
        var page = await QueryAsync();

        Assert.Equal(2, page.Page);
        Assert.Equal(10, page.PageSize);
        Assert.Equal(42, page.TotalItems);
    }

    // ---- helpers ----

    private async Task<DiscoveryPage> QueryAsync()
    {
        await using var context = _fixture.CreateContext();
        var useCase = new GetDiscoveryPageUseCase(
            new TwoShowSource(),
            new TrackedShowRepository(context));
        return await useCase.ExecuteAsync(
            DiscoveryBucket.New, new DiscoveryFilter(), page: 2, pageSize: 10, CancellationToken.None);
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

    private sealed class TwoShowSource : IDiscoverySource
    {
        public Task<DiscoveryPage> GetPageAsync(
            DiscoveryBucket bucket, DiscoveryFilter filter, int page, int pageSize, CancellationToken ct) =>
            Task.FromResult(new DiscoveryPage(
            [
                Show(TrackedTvmazeId, "Black Mirror"),
                Show(UntrackedTvmazeId, "Some Other Show"),
            ], page, pageSize, TotalItems: 42, [], [], [], []));

        // This fake models a page source only; the details cache lookup is exercised by the source's own
        // tests, so it always reports a miss here.
        public bool TryGetCached(int tvmazeId, out CatalogShow show, out IReadOnlyList<CatalogEpisode> episodes)
        {
            show = null!;
            episodes = [];
            return false;
        }
    }

    private static CatalogShow Show(int tvmazeId, string title) =>
        new(tvmazeId, title, null, null, null, null, null, [], null, null, null, null, null, null, false);
}
