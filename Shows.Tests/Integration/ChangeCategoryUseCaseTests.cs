using Microsoft.EntityFrameworkCore;
using Shows.Application.UseCases;
using Shows.Domain;
using Shows.Infrastructure.Persistence;
using Shows.Infrastructure.Persistence.Repositories;
using Shows.Infrastructure.Persistence.Sources;
using Xunit;

namespace Shows.Tests.Integration;

/// <summary>
/// Acceptance for <see cref="ChangeCategoryUseCase"/> (issue 8): the details-page category picker moves a
/// show between Started / Later / Cancelled and stamps <c>category_changed_at</c> (SPEC R6). The three
/// assertions that fail silently when wrong: the category actually changes, the timestamp moves forward
/// (R6 depends on it), <c>Finished</c> is refused as a manual target (SPEC R5 — it is automatic), and a
/// soft-deleted show is left untouched (SPEC R8 — the use case reads through the active-only find).
/// Driven by the real repositories over a temp DB; no network, no real shows.db.
/// </summary>
public sealed class ChangeCategoryUseCaseTests : IClassFixture<DatabaseFixture>, IAsyncLifetime
{
    private const int TvmazeId = 305;
    private const int DeletedTvmazeId = 306;
    private const long ShowId = 6_100_001;

    private readonly DatabaseFixture _fixture;

    public ChangeCategoryUseCaseTests(DatabaseFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync()
    {
        _fixture.Reset();
        await SeedShowAsync(ShowCategory.Started, deletedAt: null);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Moving_a_show_changes_the_category_and_stamps_category_changed_at()
    {
        DateTimeOffset originalChangedAt;
        await using (var context = _fixture.CreateContext())
        {
            originalChangedAt = (await context.UserShows.SingleAsync(u => u.TrackedShowId == ShowId))
                .CategoryChangedAt;
        }

        await using (var context = _fixture.CreateContext())
        {
            await ChangeAsync(context, ShowId, ShowCategory.Later);
        }

        await using (var context = _fixture.CreateContext())
        {
            var user = await context.UserShows.SingleAsync(u => u.TrackedShowId == ShowId);
            Assert.Equal(ShowCategory.Later, user.Category);
            Assert.True(user.CategoryChangedAt > originalChangedAt,
                "category_changed_at must move forward so R6 can compare it against the last watch change.");
        }
    }

    [Fact]
    public async Task Finished_is_refused_as_a_manual_target()
    {
        await using var context = _fixture.CreateContext();

        await Assert.ThrowsAsync<ArgumentException>(() => ChangeAsync(context, ShowId, ShowCategory.Finished));

        // The category is unchanged — the guard rejects before any write.
        await using var verify = _fixture.CreateContext();
        var user = await verify.UserShows.SingleAsync(u => u.TrackedShowId == ShowId);
        Assert.Equal(ShowCategory.Started, user.Category);
    }

    [Fact]
    public async Task A_soft_deleted_show_is_not_moved()
    {
        const long deletedShowId = 6_100_002;
        await SeedShowAsync(ShowCategory.Started, deletedAt: DateTimeOffset.UtcNow,
            trackedShowId: deletedShowId, tvmazeId: DeletedTvmazeId);

        await using var context = _fixture.CreateContext();

        // FindByIdAsync filters deleted_at IS NULL, so the use case refuses rather than mutating an
        // untracked row (SPEC R8).
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => ChangeAsync(context, deletedShowId, ShowCategory.Later));

        await using var verify = _fixture.CreateContext();
        var user = await verify.UserShows.SingleAsync(u => u.TrackedShowId == deletedShowId);
        Assert.Equal(ShowCategory.Started, user.Category); // untouched
        Assert.NotNull(user.DeletedAt); // still soft-deleted
    }

    // ---- helpers ----

    private static Task ChangeAsync(ShowsDbContext context, long trackedShowId, ShowCategory category) =>
        new ChangeCategoryUseCase(
            new TrackedShowRepository(context),
            new UserShowRepository(context),
            new UnitOfWork(context))
        .ExecuteAsync(trackedShowId, category, CancellationToken.None);

    private async Task SeedShowAsync(
        ShowCategory category, DateTimeOffset? deletedAt, long? trackedShowId = null, int tvmazeId = TvmazeId)
    {
        var id = trackedShowId ?? ShowId;
        await using var context = _fixture.CreateContext();

        context.TvmazeShows.Add(new TvmazeShow
        {
            TvmazeId = tvmazeId,
            Name = "Seed",
            StatusRaw = "Running",
            RawJson = "json",
            FetchedAt = DateTimeOffset.UtcNow,
        });

        var show = new TrackedShow
        {
            Id = id,
            TvmazeId = tvmazeId,
            Title = "Seed Show",
        };
        context.Entry(show).Property("tvmaze_show_id").CurrentValue = tvmazeId;
        show.User = new UserShow
        {
            TrackedShowId = id,
            Category = category,
            AddedAt = DateTimeOffset.UtcNow,
            CategoryChangedAt = DateTimeOffset.UtcNow,
            DeletedAt = deletedAt,
        };
        context.TrackedShows.Add(show);
        await context.SaveChangesAsync();
    }
}
