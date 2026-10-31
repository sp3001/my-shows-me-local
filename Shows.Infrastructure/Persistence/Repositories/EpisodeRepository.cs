using Microsoft.EntityFrameworkCore;
using Shows.Application;
using Shows.Domain;

namespace Shows.Infrastructure.Persistence.Repositories;

/// <summary>
/// EF implementation of <see cref="IEpisodeRepository"/>. Episodes are read in canonical order
/// (<c>air_stamp</c>, then season/number). Watch rows are the one real-delete user action — absence is
/// the meaning (SCHEMA.md §2).
/// </summary>
public sealed class EpisodeRepository(ShowsDbContext context) : IEpisodeRepository
{
    public async Task<IReadOnlyList<Episode>> ListByShowIdAsync(
        long showId, CancellationToken cancellationToken)
    {
        // SQLite cannot ORDER BY a DateTimeOffset column (EF throws), and lexicographic ordering of the
        // stored TEXT would be wrong across differing UTC offsets. A show has at most a few hundred
        // episodes, so materializing then ordering on the real instant is both correct and cheap.
        // Read-only everywhere (details grid, recompute facts, card filter) — AsNoTracking (issue 7).
        var episodes = await context.Episodes
            .Where(e => e.ShowId == showId)
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        return episodes
            .OrderBy(e => e.AirStamp)
            .ThenBy(e => e.SeasonNumber)
            .ThenBy(e => e.EpisodeNumber)
            .ToList();
    }

    public Task<Episode?> FindByIdAsync(long id, CancellationToken cancellationToken) =>
        context.Episodes.FirstOrDefaultAsync(e => e.Id == id, cancellationToken);

    public Task AddRangeAsync(IReadOnlyList<Episode> episodes, CancellationToken cancellationToken) =>
        context.Episodes.AddRangeAsync(episodes, cancellationToken);

    // Read-only everywhere (details toggles, recompute, card filter, and the toggle's wasWatched check).
    // The un-watch path (RemoveWatchAsync) does its own tracked lookup rather than reusing this list, so
    // AsNoTracking here is safe (issue 7).
    public async Task<IReadOnlyList<EpisodeWatch>> ListWatchesByShowIdAsync(
        long showId, CancellationToken cancellationToken) =>
        await context.EpisodeWatches
            .Where(w => w.ShowId == showId)
            .AsNoTracking()
            .ToListAsync(cancellationToken);

    // The batched Started-list read: every watch row of every requested show in one query. Episode ids are
    // globally unique (SPEC R13), so the caller collapses the flat result into one watched-id set.
    public async Task<IReadOnlyList<EpisodeWatch>> ListWatchesByShowIdsAsync(
        IReadOnlyList<long> showIds, CancellationToken cancellationToken) =>
        await context.EpisodeWatches
            .Where(w => showIds.Contains(w.ShowId))
            .AsNoTracking()
            .ToListAsync(cancellationToken);

    // The card's lazy per-season load. Scoped to the given season ids so a card with most seasons
    // collapsed never materializes the whole episode list (issue 7). Ordering is left to the caller.
    public async Task<IReadOnlyList<Episode>> ListBySeasonIdsAsync(
        IReadOnlyList<long> seasonIds, CancellationToken cancellationToken) =>
        await context.Episodes
            .Where(e => seasonIds.Contains(e.SeasonId))
            .AsNoTracking()
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<EpisodeMismatch>> ListMismatchesByShowIdAsync(
        long showId, CancellationToken cancellationToken) =>
        await context.EpisodeMismatches
            .Where(m => m.ShowId == showId)
            .OrderBy(m => m.SeasonNumber)
            .ThenBy(m => m.EpisodeNumber)
            .AsNoTracking()
            .ToListAsync(cancellationToken);

    public Task<int> CountWatchesByShowIdAsync(long showId, CancellationToken cancellationToken) =>
        context.EpisodeWatches.CountAsync(w => w.ShowId == showId, cancellationToken);

    // The card's eager per-season displayable count (issue 11). A grouped aggregate in raw SQL so only
    // (season_id, count) pairs cross the wire — never every episode row, which is what the lazy design
    // (issue 7) forbids loading per card. The predicate mirrors InlineEpisodeRules.IsListable exactly:
    // exclude insignificant specials (episode_type_id = 3, ADR-034), require a known air date on or
    // before today, and exclude watched episodes. Significant specials (type 2) count like regulars.
    // air_date is a DateOnly stored as ISO yyyy-MM-dd TEXT, so a lexicographic <= against
    // the same-shaped parameter is a correct date comparison (the DateTimeOffset-in-LINQ landmine does not
    // apply to DateOnly, but raw SQL keeps it unambiguous and lets the GROUP BY run server-side).
    public async Task<IReadOnlyDictionary<long, int>> CountDisplayableEpisodesBySeasonAsync(
        long showId, DateOnly today, CancellationToken cancellationToken)
    {
        var rows = await context.Database
            .SqlQueryRaw<SeasonCountRow>(
                """
                SELECT e.season_id AS [season_id], COUNT(*) AS [count]
                FROM episode e
                WHERE e.show_id = {0}
                  AND e.episode_type_id <> 3
                  AND e.air_date IS NOT NULL
                  AND e.air_date <= {1}
                  AND e.id NOT IN (SELECT w.episode_id FROM episode_watch w WHERE w.show_id = {0})
                GROUP BY e.season_id
                """,
                showId, today)
            .ToListAsync(cancellationToken);

        var map = new Dictionary<long, int>(rows.Count);
        foreach (var row in rows)
        {
            map[row.SeasonId] = checked((int)row.Count);
        }

        return map;
    }

    // Shaped result for the grouped count above. COUNT(*) boxes as Int64 through Microsoft.Data.Sqlite,
    // hence the long Count. EF binds columns to settable properties by name.
    private sealed class SeasonCountRow
    {
        public long SeasonId { get; set; }

        public long Count { get; set; }
    }

    // The batched Started-list count: the same per-season displayable count as the single-show aggregate,
    // for many shows in one grouped query. LINQ (not raw SQL) so EF translates the showIds Contains into a
    // parameterized IN and the watched check into a correlated NOT EXISTS — the raw-SQL form cannot build a
    // dynamic-length IN list. The predicate mirrors InlineEpisodeRules.IsListable exactly: exclude specials,
    // require a known air date on or before today, and exclude watched episodes (a watch row is scoped to
    // the episode's own show via the denormalised show_id). Keyed by season id, which is globally unique.
    public async Task<IReadOnlyDictionary<long, int>> CountDisplayableEpisodesBySeasonForShowsAsync(
        IReadOnlyList<long> showIds, DateOnly today, CancellationToken cancellationToken)
    {
        var rows = await context.Episodes
            .Where(e => showIds.Contains(e.ShowId)
                        && e.EpisodeType != EpisodeType.InsignificantSpecial
                        && e.AirDate != null
                        && e.AirDate <= today
                        && !context.EpisodeWatches.Any(w => w.ShowId == e.ShowId && w.EpisodeId == e.Id))
            .GroupBy(e => e.SeasonId)
            .Select(g => new { SeasonId = g.Key, Count = g.Count() })
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        var map = new Dictionary<long, int>(rows.Count);
        foreach (var row in rows)
        {
            map[row.SeasonId] = row.Count;
        }

        return map;
    }

    // Per-season runtime total for the Started card's season line ("4 · ~2h 30m"). Same displayable
    // predicate as the count above (InlineEpisodeRules.IsListable), so the header's duration matches the
    // episodes actually listed rather than the whole season. SUM ignores NULL runtime; a season whose
    // episodes all lack runtime sums to NULL and is absent from the map, so the header shows no duration
    // rather than "0 min". Batched (LINQ, not raw SQL) for the same reason as the batched count: the
    // dynamic showIds list.
    public async Task<IReadOnlyDictionary<long, int>> SumRuntimeBySeasonAsync(
        long showId, DateOnly today, CancellationToken cancellationToken) =>
        await SumRuntimeAsync(
            context.Episodes.Where(e => e.ShowId == showId), today, cancellationToken);

    public async Task<IReadOnlyDictionary<long, int>> SumRuntimeBySeasonForShowsAsync(
        IReadOnlyList<long> showIds, DateOnly today, CancellationToken cancellationToken) =>
        await SumRuntimeAsync(
            context.Episodes.Where(e => showIds.Contains(e.ShowId)), today, cancellationToken);

    // Shared body of the two runtime aggregates above: the caller supplies the show-scoped query, this
    // applies the displayable filter and groups. Runtime is summed in minutes (the stored unit) and left
    // for the UI to format — the Domain owns no display formatting for durations.
    private async Task<IReadOnlyDictionary<long, int>> SumRuntimeAsync(
        IQueryable<Episode> scoped, DateOnly today, CancellationToken cancellationToken)
    {
        var rows = await scoped
            .Where(e => e.EpisodeType != EpisodeType.InsignificantSpecial
                        && e.AirDate != null
                        && e.AirDate <= today
                        && !context.EpisodeWatches.Any(w => w.ShowId == e.ShowId && w.EpisodeId == e.Id))
            .GroupBy(e => e.SeasonId)
            .Select(g => new { SeasonId = g.Key, Minutes = g.Sum(e => e.RuntimeMinutes) })
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        var map = new Dictionary<long, int>(rows.Count);
        foreach (var row in rows)
        {
            if (row.Minutes is { } minutes && minutes > 0)
            {
                map[row.SeasonId] = minutes;
            }
        }

        return map;
    }

    // The most recent already-aired episode date per show (improvement 12). air_date is a DateOnly stored as
    // ISO yyyy-MM-dd TEXT, so a lexicographic MAX is a correct date max (the DateTimeOffset-in-LINQ landmine
    // does not apply to DateOnly). Filtered to aired-on-or-before-today so a future episode is not "last
    // aired"; a show with none is absent and sorts last.
    public async Task<IReadOnlyDictionary<long, DateOnly>> LastAiredByShowIdsAsync(
        IReadOnlyList<long> showIds, DateOnly today, CancellationToken cancellationToken)
    {
        var rows = await context.Episodes
            .Where(e => showIds.Contains(e.ShowId)
                        && e.AirDate != null
                        && e.AirDate <= today)
            .GroupBy(e => e.ShowId)
            .Select(g => new { ShowId = g.Key, LastAired = g.Max(e => e.AirDate) })
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        var map = new Dictionary<long, DateOnly>(rows.Count);
        foreach (var row in rows)
        {
            if (row.LastAired is { } lastAired)
            {
                map[row.ShowId] = lastAired;
            }
        }

        return map;
    }

    // The upcoming regular-or-significant-special episodes of many shows (improvement 4's upcoming view):
    // an episode whose air date is unknown (null) or strictly after today. Insignificant specials excluded
    // (SPEC R4 / ADR-034); significant specials count like regulars. air_date is ISO yyyy-MM-dd TEXT, so
    // the lexicographic > against the same-shaped parameter is a correct date comparison. Ordering
    // is left to the caller (SQLite cannot ORDER BY a DateTimeOffset; the caller sorts by AirStamp).
    public async Task<IReadOnlyList<Episode>> ListUpcomingByShowIdsAsync(
        IReadOnlyList<long> showIds, DateOnly today, CancellationToken cancellationToken) =>
        await context.Episodes
            .Where(e => showIds.Contains(e.ShowId)
                        && e.EpisodeType != EpisodeType.InsignificantSpecial
                        && (e.AirDate == null || e.AirDate > today))
            .AsNoTracking()
            .ToListAsync(cancellationToken);

    public Task AddWatchAsync(EpisodeWatch watch, CancellationToken cancellationToken) =>
        context.EpisodeWatches.AddAsync(watch, cancellationToken).AsTask();

    public async Task<bool> RemoveWatchAsync(long episodeId, CancellationToken cancellationToken)
    {
        // Returns whether a row was actually removed, so a double-unwatch is not a silent no-op success.
        var watch = await context.EpisodeWatches
            .FirstOrDefaultAsync(w => w.EpisodeId == episodeId, cancellationToken);
        if (watch is null)
        {
            return false;
        }

        context.EpisodeWatches.Remove(watch);
        return true;
    }
}
