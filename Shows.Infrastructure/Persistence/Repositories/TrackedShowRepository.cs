using Microsoft.EntityFrameworkCore;
using Shows.Application;
using Shows.Domain;

namespace Shows.Infrastructure.Persistence.Repositories;

/// <summary>
/// EF implementation of <see cref="ITrackedShowRepository"/>. Assembles the
/// <see cref="TrackedShow"/> + <see cref="UserShow"/> aggregate here, so the
/// <c>deleted_at IS NULL</c> predicate exists in exactly one place (<see cref="ActiveShowFilter"/>).
/// Returns Domain types; never <c>IQueryable</c> (ARCHITECTURE.md §3).
/// </summary>
public sealed class TrackedShowRepository(ShowsDbContext context) : ITrackedShowRepository
{
    public Task<TrackedShow?> FindByIdAsync(long id, CancellationToken cancellationToken) =>
        Active().FirstOrDefaultAsync(s => s.Id == id, cancellationToken);

    // The details-page read (SPEC F5). Includes Genres and Seasons on top of the User include; the
    // hot-path FindByIdAsync deliberately omits both joins because the jobs and the recompute never
    // read them. Seasons is required here: the details VM groups episodes by iterating
    // TrackedShow.Seasons (the episodes themselves arrive through a separate repository call), so an
    // unloaded Seasons navigation renders an empty grid even when episode rows exist (issue 2).
    // Read-only display read (issue 7): AsNoTracking. The details page never mutates the entity it gets
    // here — every mutation (category change, untrack, sync) re-fetches through its own use case via the
    // tracked FindByIdAsync — so change-tracking this aggregate is pure overhead.
    public Task<TrackedShow?> FindByIdWithGenresAsync(long id, CancellationToken cancellationToken) =>
        WithUser()
            .Include(s => s.Genres)
            .Include(s => s.Seasons)
            .Where(ActiveShowFilter.Predicate)
            .AsNoTracking()
            // The only two-collection read in the app (Genres + Seasons). Split it explicitly rather than
            // relying on a global SplitQuery: a global setting breaks the mapper's explicit collection
            // loads (see ShowsInfrastructureServiceCollectionExtensions). AsNoTracking keeps this read-only
            // display read out of the change tracker, so it never feeds a split-query entity back into the
            // mapper's Collection(...).LoadAsync path.
            .AsSplitQuery()
            .FirstOrDefaultAsync(s => s.Id == id, cancellationToken);

    public Task<TrackedShow?> FindByTvmazeIdAsync(int tvmazeId, CancellationToken cancellationToken) =>
        Active().FirstOrDefaultAsync(s => s.TvmazeId == tvmazeId, cancellationToken);

    // WithUser() (not Active()) — no deleted_at filter, so a soft-deleted show is found for restore
    // (step 3.2). The caller inspects User.DeletedAt to decide restore vs. already-tracked.
    public Task<TrackedShow?> FindByTvmazeIdIncludingDeletedAsync(
        int tvmazeId, CancellationToken cancellationToken) =>
        WithUser().FirstOrDefaultAsync(s => s.TvmazeId == tvmazeId, cancellationToken);

    public async Task<IReadOnlyList<TrackedShow>> ListActiveAsync(CancellationToken cancellationToken) =>
        await Active().OrderBy(s => s.Title).ToListAsync(cancellationToken);

    // The category-tab list read (issue 7). AsNoTracking: a tab can hold hundreds of shows and the list
    // only displays them — the card and every mutation re-fetch what they need through their own use
    // case. Tracking hundreds of TrackedShow+UserShow aggregates for a read-only list is pure overhead.
    public async Task<IReadOnlyList<TrackedShow>> ListActiveByCategoryAsync(
        ShowCategory category, CancellationToken cancellationToken) =>
        await Active()
            .Include(s => s.Genres)
            .Where(s => s.User!.Category == category)
            .OrderBy(s => s.Title)
            .AsNoTracking()
            // Two collections now (User + Genres); split explicitly so the many-to-many join does not
            // fan out into a cartesian product (same rationale as FindByIdWithGenresAsync).
            .AsSplitQuery()
            .ToListAsync(cancellationToken);

    // The job selection query (SPEC R8/R12). Active() already applies deleted_at IS NULL; the status
    // filter is on the enum column, never a raw string. The shared selection lives here so the four
    // jobs cannot each forget the deleted_at filter and silently spend API calls on untracked shows.
    public async Task<IReadOnlyList<TrackedShow>> ListActiveByStatusAsync(
        IReadOnlyCollection<ShowStatus> statuses, CancellationToken cancellationToken) =>
        await Active()
            .Where(s => statuses.Contains(s.Status))
            .OrderBy(s => s.Title)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<TrackedShow>> ListActiveExcludingStatusAsync(
        IReadOnlyCollection<ShowStatus> statuses, CancellationToken cancellationToken) =>
        await Active()
            .Where(s => !statuses.Contains(s.Status))
            .OrderBy(s => s.Title)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<TrackedShow>> ListIncludingUntrackedAsync(
        CancellationToken cancellationToken) =>
        await WithUser().OrderBy(s => s.Title).ToListAsync(cancellationToken);

    public async Task<Dictionary<int, long?>> GetActiveTrackingByTvmazeIdsAsync(
        IReadOnlyList<int> tvmazeIds, CancellationToken cancellationToken)
    {
        // Reads user_show filtered deleted_at IS NULL, NOT tracked_show (SPEC F2): a soft-deleted show
        // still has a tracked_show row, so checking that table would report it as tracked and block the
        // re-add the design is built around. The value is the tracked_show id (the user row's shared PK),
        // so a tracked card can link to its tracked-details page; a not-tracked id maps to null.
        var active = await context.UserShows
            .Where(u => u.DeletedAt == null && tvmazeIds.Contains(u.Show!.TvmazeId))
            .Select(u => new { TvmazeId = u.Show!.TvmazeId, u.TrackedShowId })
            .ToListAsync(cancellationToken);

        var byTvmazeId = active.ToDictionary(a => a.TvmazeId, a => (long?)a.TrackedShowId);
        return tvmazeIds.Distinct().ToDictionary(id => id, id => byTvmazeId.TryGetValue(id, out var trackedId) ? trackedId : null);
    }

    // The light card read: only the season rows, ordered by number. No genres, no episodes, no user row —
    // the card already has the TrackedShow from the category read and needs just the season headers here.
    public async Task<IReadOnlyList<Season>> ListSeasonsByShowIdAsync(
        long showId, CancellationToken cancellationToken) =>
        await context.Seasons
            .Where(s => s.ShowId == showId)
            .OrderBy(s => s.SeasonNumber)
            .AsNoTracking()
            .ToListAsync(cancellationToken);

    // The batched Started-list read: every season of every requested show in one query. EF translates the
    // Contains into a parameterized IN; the caller groups the flat result by Season.ShowId.
    public async Task<IReadOnlyList<Season>> ListSeasonsByShowIdsAsync(
        IReadOnlyList<long> showIds, CancellationToken cancellationToken) =>
        await context.Seasons
            .Where(s => showIds.Contains(s.ShowId))
            .OrderBy(s => s.ShowId)
            .ThenBy(s => s.SeasonNumber)
            .AsNoTracking()
            .ToListAsync(cancellationToken);

    public async Task AddAsync(TrackedShow show, CancellationToken cancellationToken)
    {
        // tracked_show.tvmaze_show_id is a NOT NULL FK (SCHEMA §2 — a mapped row cannot exist without
        // its source row, SPEC P3/P4), so the shell cannot be inserted until a tvmaze_show row exists
        // to point at. The caller (AddShowToTrackingUseCase) creates that source row through the mapper
        // — the sole writer of source rows (SPEC R11) — before calling AddAsync. Here we only point the
        // shadow FK at it, by property name: setting it by string keeps the repository from referencing
        // the source entity type, which source-table containment forbids (step 8.1).
        context.Entry(show).Property<int>("tvmaze_show_id").CurrentValue = show.TvmazeId;
        await context.TrackedShows.AddAsync(show, cancellationToken).ConfigureAwait(false);
    }

    // The default read: the aggregate with its user row, filtered to active. The User include is what
    // makes TrackedShow.Category resolvable without a second query.
    private IQueryable<TrackedShow> Active() => WithUser().Where(ActiveShowFilter.Predicate);

    private IQueryable<TrackedShow> WithUser() =>
        context.TrackedShows.Include(s => s.User);
}
