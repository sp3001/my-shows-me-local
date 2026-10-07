using Shows.Application;
using Shows.Domain;
using Shows.Infrastructure.Persistence;
using Shows.Infrastructure.Persistence.Repositories;
using Shows.Infrastructure.Persistence.Sources;
using Xunit;

namespace Shows.Tests.Integration;

/// <summary>
/// Step 1.4 acceptance: the <c>deleted_at IS NULL</c> predicate is applied by the repository and is
/// not a hard-coded assumption. Soft-deleting a show must hide it from the default read path while the
/// explicit "include deleted" path still sees it — proving the filter is real and lives in one place
/// (SPEC R8, ADR-024).
/// </summary>
public sealed class RepositoryTests : IClassFixture<DatabaseFixture>, IAsyncLifetime
{
    private readonly DatabaseFixture _fixture;
    private long _showId;

    public RepositoryTests(DatabaseFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync()
    {
        _fixture.Reset();
        _showId = 9_000_001;

        // Seed an actively-tracked show through the repositories + unit of work, not raw SQL — this
        // exercises the write path too.
        await using var scope = _fixture.CreateContext();
        scope.TvmazeShows.Add(new TvmazeShow
        {
            TvmazeId = 12345,
            Name = "Filter Test",
            StatusRaw = "Running",
            RawJson = "json",
            FetchedAt = DateTimeOffset.UtcNow,
        });

        var show = new TrackedShow { Id = _showId, TvmazeId = 12345, Title = "Filter Test" };
        scope.Entry(show).Property("tvmaze_show_id").CurrentValue = 12345;
        show.User = new UserShow
        {
            TrackedShowId = _showId,
            Category = ShowCategory.Started,
            AddedAt = DateTimeOffset.UtcNow,
            CategoryChangedAt = DateTimeOffset.UtcNow,
        };
        scope.TrackedShows.Add(show);
        await scope.SaveChangesAsync(default);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Default_read_sees_an_active_show()
    {
        await using var context = _fixture.CreateContext();
        var repo = new TrackedShowRepository(context);

        var found = await repo.FindByIdAsync(_showId, default);
        Assert.NotNull(found);
        Assert.False(found!.IsDeleted);
        Assert.Equal(ShowCategory.Started, found.Category);
    }

    [Fact]
    public async Task Soft_delete_hides_from_default_read_but_not_from_include_deleted()
    {
        await using (var context = _fixture.CreateContext())
        {
            var users = new UserShowRepository(context);
            await users.SoftDeleteAsync(_showId, DateTimeOffset.UtcNow, default);
            await context.SaveChangesAsync(default);
        }

        await using (var context = _fixture.CreateContext())
        {
            var repo = new TrackedShowRepository(context);

            // The default read path cannot see it.
            Assert.Null(await repo.FindByIdAsync(_showId, default));
            Assert.DoesNotContain(_showId, (await repo.ListActiveAsync(default)).Select(s => s.Id));
            Assert.DoesNotContain(
                _showId, (await repo.ListActiveByCategoryAsync(ShowCategory.Started, default)).Select(s => s.Id));

            // The explicit escape hatch can.
            Assert.Contains(_showId, (await repo.ListIncludingUntrackedAsync(default)).Select(s => s.Id));
        }
    }

    [Fact]
    public async Task GetActiveTracking_reports_a_soft_deleted_show_as_untracked()
    {
        // SPEC F2: the "already tracked" marker reads user_show, not tracked_show. A soft-deleted show
        // still has a tracked_show row, so it must report as NOT tracked — otherwise re-add is blocked.
        await using (var context = _fixture.CreateContext())
        {
            var users = new UserShowRepository(context);
            await users.SoftDeleteAsync(_showId, DateTimeOffset.UtcNow, default);
            await context.SaveChangesAsync(default);
        }

        await using (var context = _fixture.CreateContext())
        {
            var repo = new TrackedShowRepository(context);
            var tracking = await repo.GetActiveTrackingByTvmazeIdsAsync([12345], default);
            // A soft-deleted show reports no tracked-show id (null), so a card treats it as untracked.
            Assert.Null(tracking[12345]);
        }
    }

    [Fact]
    public async Task Restore_makes_the_show_visible_again()
    {
        await using (var context = _fixture.CreateContext())
        {
            var users = new UserShowRepository(context);
            await users.SoftDeleteAsync(_showId, DateTimeOffset.UtcNow, default);
            await context.SaveChangesAsync(default);
        }

        await using (var context = _fixture.CreateContext())
        {
            var users = new UserShowRepository(context);
            await users.RestoreAsync(_showId, default);
            await context.SaveChangesAsync(default);
        }

        await using (var context = _fixture.CreateContext())
        {
            var repo = new TrackedShowRepository(context);
            Assert.NotNull(await repo.FindByIdAsync(_showId, default));
        }
    }

    [Fact]
    public async Task Change_category_updates_the_user_row()
    {
        await using (var context = _fixture.CreateContext())
        {
            var users = new UserShowRepository(context);
            await users.ChangeCategoryAsync(_showId, ShowCategory.Later, DateTimeOffset.UtcNow, default);
            await context.SaveChangesAsync(default);
        }

        await using (var context = _fixture.CreateContext())
        {
            var repo = new TrackedShowRepository(context);
            var found = await repo.FindByIdAsync(_showId, default);
            Assert.Equal(ShowCategory.Later, found!.Category);
        }
    }

    [Fact]
    public async Task UnitOfWork_commits_pending_changes()
    {
        await using var context = _fixture.CreateContext();
        var repo = new EpisodeRepository(context);
        var uow = new UnitOfWork(context);

        var season = new Season { Id = _showId + 1, ShowId = _showId, SeasonNumber = 1 };
        context.Seasons.Add(season);
        await repo.AddRangeAsync(
            [new Episode { Id = _showId + 2, ShowId = _showId, SeasonId = season.Id, SeasonNumber = 1, EpisodeNumber = 1 }],
            default);

        var before = await repo.ListByShowIdAsync(_showId, default);
        Assert.Empty(before); // not yet committed

        await uow.SaveChangesAsync(default);

        var after = await repo.ListByShowIdAsync(_showId, default);
        Assert.Single(after);
    }
}
