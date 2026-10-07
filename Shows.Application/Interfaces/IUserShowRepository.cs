using Shows.Domain;

namespace Shows.Application;

/// <summary>
/// Writes the user's tracking decision — the only irreplaceable show-level data (SPEC P4).
/// </summary>
/// <remarks>
/// Separate from <see cref="ITrackedShowRepository"/> because the two rows have different owners and
/// lifetimes (ADR-024): the mapper writes <c>tracked_show</c> and may never write <c>user_show</c>.
/// Splitting the interfaces is what makes that rule expressible in a type signature.
/// </remarks>
public interface IUserShowRepository
{
    /// <summary>
    /// Reads the user row even when soft-deleted. Needed by the re-add path, which must find the
    /// existing row to clear <c>deleted_at</c> rather than insert a second one (SPEC R8).
    /// </summary>
    Task<UserShow?> FindByTrackedShowIdAsync(long trackedShowId, CancellationToken cancellationToken);

    Task AddAsync(UserShow userShow, CancellationToken cancellationToken);

    /// <summary>
    /// Sets <c>deleted_at</c>. Deletes nothing (SPEC R8, ADR-024) — the show's seasons, episodes and
    /// watch rows all survive so re-adding restores the exact watched state.
    /// </summary>
    Task SoftDeleteAsync(long trackedShowId, DateTimeOffset at, CancellationToken cancellationToken);

    /// <summary>
    /// Clears <c>deleted_at</c> and leaves <c>added_at</c> and <c>category_id</c> alone: the original
    /// tracking date is history, not a cache (step 3.2).
    /// </summary>
    Task RestoreAsync(long trackedShowId, CancellationToken cancellationToken);

    Task ChangeCategoryAsync(
        long trackedShowId, ShowCategory category, DateTimeOffset at, CancellationToken cancellationToken);

    /// <summary>
    /// Sets the pinned flag (improvement 18). A pinned show sorts to the top of the Started list above the
    /// active sort. Stored here, not in a UI preference, because pinning is a user decision (SPEC P4) that
    /// must survive a relaunch and a rebuild.
    /// </summary>
    Task SetPinnedAsync(
        long trackedShowId, bool isPinned, CancellationToken cancellationToken);

    /// <summary>
    /// Sets the user's own 1-10 rating (null clears it). User data (SPEC P4), so it is stored here and
    /// survives a rebuild. Orthogonal to category and the logical-delete state, so neither is touched.
    /// </summary>
    Task SetUserRatingAsync(
        long trackedShowId, int? userRating, CancellationToken cancellationToken);

    Task ClearNeedsReviewAsync(long trackedShowId, CancellationToken cancellationToken);
}
