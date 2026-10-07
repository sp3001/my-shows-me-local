namespace Shows.Application.UseCases;

/// <summary>
/// Pins or unpins a tracked show (improvement 18). A pinned show sorts to the top of the Started list,
/// above the active sort, so the user's most-watched shows stay reachable regardless of sort order.
/// </summary>
/// <remarks>
/// A tiny write use case rather than letting the ViewModel reach <see cref="IUserShowRepository"/> directly:
/// ViewModels talk only to use cases (<c>ARCHITECTURE.md</c> §3), and this keeps the one write to
/// <c>user_show.is_pinned</c> behind the same seam as every other mutation. Pinning is a user decision
/// (SPEC P4), so it is stored, not held in a UI preference like the sort choice — it must survive a
/// relaunch and a rebuild. It is orthogonal to category and the logical-delete state, so neither is touched.
/// </remarks>
public sealed class SetShowPinnedUseCase(
    IUserShowRepository userShows,
    IUnitOfWork unitOfWork)
{
    /// <summary>
    /// Sets the pinned flag and returns the new value, so the caller can reflect it without a reload. The
    /// repository requires the <c>user_show</c> row to exist (SPEC P4), so pinning an untracked show throws
    /// rather than silently doing nothing.
    /// </summary>
    public async Task<bool> ExecuteAsync(
        long trackedShowId, bool isPinned, CancellationToken cancellationToken)
    {
        await userShows
            .SetPinnedAsync(trackedShowId, isPinned, cancellationToken)
            .ConfigureAwait(false);
        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return isPinned;
    }
}
