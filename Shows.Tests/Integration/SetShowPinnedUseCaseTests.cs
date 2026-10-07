using Microsoft.EntityFrameworkCore;
using Shows.Application.UseCases;
using Shows.Domain;
using Shows.Infrastructure.Persistence;
using Shows.Infrastructure.Persistence.Repositories;
using Shows.Infrastructure.Persistence.Sources;
using Xunit;

namespace Shows.Tests.Integration;

/// <summary>
/// Acceptance for <see cref="SetShowPinnedUseCase"/> (improvement 18): pinning a tracked show sets
/// <c>user_show.is_pinned</c>, unpinning clears it, and the write is orthogonal to the other user fields —
/// pinning must not disturb <c>category_id</c> or the logical-delete state (SPEC P4: pin is its own decision,
/// not a side effect of category or tracking state). Driven by the real repositories over a temp DB; no
/// network, no real shows.db.
/// </summary>
public sealed class SetShowPinnedUseCaseTests : IClassFixture<DatabaseFixture>, IAsyncLifetime
{
    private const int TvmazeId = 307;
    private const long ShowId = 6_300_001;

    private readonly DatabaseFixture _fixture;

    public SetShowPinnedUseCaseTests(DatabaseFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync()
    {
        _fixture.Reset();
        await SeedShowAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Pinning_sets_the_flag_and_returns_the_new_value()
    {
        await using (var context = _fixture.CreateContext())
        {
            var result = await PinAsync(context, ShowId, isPinned: true);
            Assert.True(result);
        }

        await using var verify = _fixture.CreateContext();
        var user = await verify.UserShows.SingleAsync(u => u.TrackedShowId == ShowId);
        Assert.True(user.IsPinned);
    }

    [Fact]
    public async Task Unpinning_clears_the_flag()
    {
        await using (var context = _fixture.CreateContext())
        {
            await PinAsync(context, ShowId, isPinned: true);
        }

        await using (var context = _fixture.CreateContext())
        {
            var result = await PinAsync(context, ShowId, isPinned: false);
            Assert.False(result);
        }

        await using var verify = _fixture.CreateContext();
        var user = await verify.UserShows.SingleAsync(u => u.TrackedShowId == ShowId);
        Assert.False(user.IsPinned);
    }

    // Pinning is orthogonal to the other user decisions (SPEC P4): it must not touch category or the
    // logical-delete state. A show pinned while Started stays Started and stays actively tracked.
    [Fact]
    public async Task Pinning_does_not_disturb_category_or_delete_state()
    {
        await using var context = _fixture.CreateContext();
        await PinAsync(context, ShowId, isPinned: true);

        await using var verify = _fixture.CreateContext();
        var user = await verify.UserShows.SingleAsync(u => u.TrackedShowId == ShowId);
        Assert.Equal(ShowCategory.Started, user.Category);
        Assert.Null(user.DeletedAt);
    }

    // ---- helpers ----

    private static Task<bool> PinAsync(ShowsDbContext context, long trackedShowId, bool isPinned) =>
        new SetShowPinnedUseCase(new UserShowRepository(context), new UnitOfWork(context))
            .ExecuteAsync(trackedShowId, isPinned, CancellationToken.None);

    private async Task SeedShowAsync()
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

        var show = new TrackedShow
        {
            Id = ShowId,
            TvmazeId = TvmazeId,
            Title = "Seed Show",
        };
        context.Entry(show).Property("tvmaze_show_id").CurrentValue = TvmazeId;
        show.User = new UserShow
        {
            TrackedShowId = ShowId,
            Category = ShowCategory.Started,
            AddedAt = DateTimeOffset.UtcNow,
            CategoryChangedAt = DateTimeOffset.UtcNow,
        };
        context.TrackedShows.Add(show);
        await context.SaveChangesAsync();
    }
}
