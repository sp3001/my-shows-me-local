using Shows.Domain;

namespace Shows.Application.UseCases;

/// <summary>
/// Adds a show to a category (SPEC R8). <b>Adding a show is Enrichment scoped to one show</b>
/// (ADR-023): this creates the two rows the mapper is forbidden to create, then hands everything else
/// to <see cref="IShowMapper"/>. It writes no show or episode field itself.
/// </summary>
/// <remarks>
/// <para>
/// The two-row split is the whole point of ADR-024: <c>tracked_show</c> is mapper-owned and
/// rebuildable, <c>user_show</c> is user-owned and irreplaceable. This is the only place both are
/// created, which is what keeps SPEC P4 testable.
/// </para>
/// <para>
/// <b>The restore path is the interesting case.</b> If a <c>user_show</c> row already exists with
/// <c>deleted_at</c> set, adding again clears the flag rather than inserting. It does not reset
/// <c>added_at</c> — the user's original tracking date is history, not a cache — and it does not reset
/// the category, because the episode ids were never regenerated so the watch marks are still exactly
/// where they were.
/// </para>
/// </remarks>
public sealed class AddShowToTrackingUseCase(
    ICatalogProvider provider,
    ITrackedShowRepository trackedShows,
    IUserShowRepository userShows,
    IShowMapper mapper,
    IIdGenerator idGenerator,
    IUnitOfWork unitOfWork)
{
    /// <summary>
    /// Returns the show after mapping, so the caller can render it without a second read.
    /// </summary>
    public async Task<TrackedShow> ExecuteAsync(
        int tvmazeId, ShowCategory category, CancellationToken cancellationToken)
    {
        // Include-deleted: a soft-deleted show still has a tracked_show row, and the restore path must
        // find it. FindByTvmazeIdAsync would filter it out and send us down the new-show path into a
        // UNIQUE tvmaze_id violation (step 3.2, ADR-024).
        var existing = await trackedShows.FindByTvmazeIdIncludingDeletedAsync(tvmazeId, cancellationToken)
            .ConfigureAwait(false);

        if (existing is not null)
        {
            var existingUser = await userShows
                .FindByTrackedShowIdAsync(existing.Id, cancellationToken)
                .ConfigureAwait(false);

            if (existingUser?.DeletedAt is not null)
            {
                // Re-add: restore, do not insert. Category and added_at survive (ADR-024).
                await userShows.RestoreAsync(existing.Id, cancellationToken).ConfigureAwait(false);
            }
            else if (existingUser is not null)
            {
                // Already tracked. Idempotent: no category reset, no watch-mark change (step 3.2).
                return existing;
            }

            // Still map: a show can be tracked with stale source rows, and refreshing them is the
            // mapper's job rather than a reason to refuse the add.
            await mapper.MapShowAsync(tvmazeId, cancellationToken).ConfigureAwait(false);
            await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            return await trackedShows
                    .FindByTvmazeIdAsync(tvmazeId, cancellationToken)
                    .ConfigureAwait(false)
                ?? existing;
        }

        // SPEC P2: before this moment the show does not exist locally at all.
        var catalog = await provider.GetShowAsync(tvmazeId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"Catalog provider has no show {tvmazeId}; nothing was written.");

        var now = DateTimeOffset.UtcNow;
        var showId = idGenerator.NextId();

        // Only the identity and the ids the mapper needs are written here. Every catalog field —
        // title, status, runtime, poster — is the mapper's to fill (SPEC R11). Writing them here
        // would be the second code path R11 exists to prevent.
        //
        // The source row is minted through the mapper first: tracked_show.tvmaze_show_id is a NOT NULL
        // FK (SCHEMA §2), and the mapper is the sole writer of source rows (SPEC R11), so the shell it
        // points at must come from the mapper, not from this layer or the repository. MapShowAsync below
        // overwrites the shell with the real payload.
        await mapper.EnsureSourceRowAsync(tvmazeId, cancellationToken).ConfigureAwait(false);

        var show = new TrackedShow { Id = showId, TvmazeId = catalog.TvmazeId };
        await trackedShows.AddAsync(show, cancellationToken).ConfigureAwait(false);

        await userShows.AddAsync(
            new UserShow
            {
                TrackedShowId = showId,
                Category = category,
                AddedAt = now,
                CategoryChangedAt = now,
            },
            cancellationToken).ConfigureAwait(false);

        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        await mapper.MapShowAsync(tvmazeId, cancellationToken).ConfigureAwait(false);
        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return await trackedShows.FindByTvmazeIdAsync(tvmazeId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"Show {tvmazeId} disappeared between add and read.");
    }
}

/// <summary>
/// Untracks a show — a logical delete, nothing more (SPEC R8, ADR-024).
/// </summary>
/// <remarks>
/// Kept next to the add path because the two must stay symmetric: this sets one column, and the
/// counterpart clears it. Its counterpart in the UI must not be phrased as a deletion, and there is no
/// permanent-delete feature in v1.
/// </remarks>
public sealed class UntrackShowUseCase(
    ITrackedShowRepository trackedShows,
    IUserShowRepository userShows,
    IUnitOfWork unitOfWork)
{
    public async Task ExecuteAsync(long trackedShowId, CancellationToken cancellationToken)
    {
        var show = await trackedShows.FindByIdAsync(trackedShowId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException($"No tracked show with id {trackedShowId}.");

        await userShows.SoftDeleteAsync(show.Id, DateTimeOffset.UtcNow, cancellationToken)
            .ConfigureAwait(false);
        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}
