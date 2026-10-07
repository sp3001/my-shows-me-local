using Shows.Domain;

namespace Shows.Application;

/// <summary>
/// Reads and writes per-show, per-season expansion state.
/// </summary>
/// <remarks>
/// User-owned (SPEC P4) and untouched by untracking. Persisted rather than held in memory so all
/// seasons start collapsed on a fresh launch and the user's expansions come back (SPEC R9).
/// </remarks>
public interface IUiStateRepository
{
    Task<IReadOnlyList<UiSeasonState>> ListByShowIdAsync(long showId, CancellationToken cancellationToken);

    /// <summary>
    /// The expansion rows of many shows at once, for the batched Started-list read. Each row carries its
    /// <see cref="UiSeasonState.ShowId"/>, so the caller groups the flat result by show.
    /// </summary>
    Task<IReadOnlyList<UiSeasonState>> ListByShowIdsAsync(
        IReadOnlyList<long> showIds, CancellationToken cancellationToken);

    /// <summary>
    /// Insert-or-update for one (show, season) pair. <paramref name="id"/> is a caller-minted surrogate
    /// key (ADR-027 — <c>ui_season_state.id</c> is <c>ValueGenerated.Never</c>) used only when a row does
    /// not yet exist; the update path ignores it. Minting here rather than defaulting to 0 is what lets a
    /// show have more than one expanded season — a second insert with the default 0 would collide on the PK.
    /// </summary>
    Task SetExpandedAsync(
        long showId, long seasonId, bool isExpanded, long id, CancellationToken cancellationToken);
}
