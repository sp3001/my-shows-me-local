using Microsoft.EntityFrameworkCore;
using Shows.Application;
using Shows.Domain;

namespace Shows.Infrastructure.Persistence.Repositories;

/// <summary>
/// EF implementation of <see cref="IUserShowRepository"/> — the only writer of the user's tracking
/// decision (SPEC P4). Separate from the show repository because the mapper may never write this table
/// (ADR-023); the split is what makes that rule expressible in a type signature.
/// </summary>
public sealed class UserShowRepository(ShowsDbContext context) : IUserShowRepository
{
    // Reads even when soft-deleted: the re-add path must find the existing row to clear deleted_at
    // rather than insert a second one (SPEC R8). No ActiveShowFilter here — that is the point.
    public Task<UserShow?> FindByTrackedShowIdAsync(
        long trackedShowId, CancellationToken cancellationToken) =>
        context.UserShows.FirstOrDefaultAsync(u => u.TrackedShowId == trackedShowId, cancellationToken);

    public Task AddAsync(UserShow userShow, CancellationToken cancellationToken) =>
        context.UserShows.AddAsync(userShow, cancellationToken).AsTask();

    public async Task SoftDeleteAsync(
        long trackedShowId, DateTimeOffset at, CancellationToken cancellationToken)
    {
        // Sets deleted_at; deletes nothing (SPEC R8, ADR-024). Seasons, episodes and watch rows survive.
        var user = await FindRequiredAsync(trackedShowId, cancellationToken);
        user.DeletedAt = at;
    }

    public async Task RestoreAsync(long trackedShowId, CancellationToken cancellationToken)
    {
        // Clears deleted_at and leaves added_at and category_id alone — the original tracking date is
        // history, not a cache (step 3.2).
        var user = await FindRequiredAsync(trackedShowId, cancellationToken);
        user.DeletedAt = null;
    }

    public async Task ChangeCategoryAsync(
        long trackedShowId, ShowCategory category, DateTimeOffset at, CancellationToken cancellationToken)
    {
        var user = await FindRequiredAsync(trackedShowId, cancellationToken);
        user.Category = category;
        user.CategoryChangedAt = at;
    }

    public async Task SetPinnedAsync(
        long trackedShowId, bool isPinned, CancellationToken cancellationToken)
    {
        // Improvement 18: pin/unpin. Only the flag changes — pinning is orthogonal to category and the
        // logical-delete state, so it must not touch either.
        var user = await FindRequiredAsync(trackedShowId, cancellationToken);
        user.IsPinned = isPinned;
    }

    public async Task SetUserRatingAsync(
        long trackedShowId, int? userRating, CancellationToken cancellationToken)
    {
        // The user's own 1-10 rating. Only the rating changes — orthogonal to category and the
        // logical-delete state, so it must not touch either. The 1-10 range is validated by the use case.
        var user = await FindRequiredAsync(trackedShowId, cancellationToken);
        user.UserRating = userRating;
    }

    public async Task ClearNeedsReviewAsync(long trackedShowId, CancellationToken cancellationToken)
    {
        var user = await FindRequiredAsync(trackedShowId, cancellationToken);
        user.NeedsReview = false;
    }

    private async Task<UserShow> FindRequiredAsync(long trackedShowId, CancellationToken cancellationToken)
    {
        var user = await FindByTrackedShowIdAsync(trackedShowId, cancellationToken)
            ?? throw new InvalidOperationException(
                $"No user_show row for tracked_show {trackedShowId} (SPEC P4).");
        return user;
    }
}
