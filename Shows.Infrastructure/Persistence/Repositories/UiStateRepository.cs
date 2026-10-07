using Microsoft.EntityFrameworkCore;
using Shows.Application;
using Shows.Domain;

namespace Shows.Infrastructure.Persistence.Repositories;

/// <summary>
/// EF implementation of <see cref="IUiStateRepository"/>. Per-show, per-season expansion state
/// (ADR-012), user-owned and untouched by untracking.
/// </summary>
public sealed class UiStateRepository(ShowsDbContext context) : IUiStateRepository
{
    public async Task<IReadOnlyList<UiSeasonState>> ListByShowIdAsync(
        long showId, CancellationToken cancellationToken) =>
        await context.UiSeasonStates
            .Where(u => u.ShowId == showId)
            .ToListAsync(cancellationToken);

    // The batched Started-list read: every expansion row of every requested show in one query. The caller
    // groups the flat result by UiSeasonState.ShowId.
    public async Task<IReadOnlyList<UiSeasonState>> ListByShowIdsAsync(
        IReadOnlyList<long> showIds, CancellationToken cancellationToken) =>
        await context.UiSeasonStates
            .Where(u => showIds.Contains(u.ShowId))
            .ToListAsync(cancellationToken);

    public async Task SetExpandedAsync(
        long showId, long seasonId, bool isExpanded, long id, CancellationToken cancellationToken)
    {
        // Insert-or-update for one (show, season) pair. The id is caller-minted (ADR-027): the entity's
        // PK is ValueGenerated.Never, so leaving it at the default 0 would let the first row insert but
        // collide on every subsequent one — the bug that silently broke multi-season expansion.
        var state = await context.UiSeasonStates
            .FirstOrDefaultAsync(u => u.ShowId == showId && u.SeasonId == seasonId, cancellationToken);

        if (state is null)
        {
            context.UiSeasonStates.Add(new UiSeasonState
            {
                Id = id,
                ShowId = showId,
                SeasonId = seasonId,
                IsExpanded = isExpanded,
            });
        }
        else
        {
            state.IsExpanded = isExpanded;
        }
    }
}
