using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Shows.Infrastructure.Persistence;

namespace Shows.Infrastructure.Discovery;

/// <summary>One retained raw snapshot, keyed by the UTC day it was fetched.</summary>
internal sealed record ScheduleArchiveFile(DateOnly FetchedOn);

/// <summary>
/// The dated raw <c>/schedule/full</c> archive that gives Discovery's look-back window a past.
/// Captures live in SQLite and are reparsed by the current schedule parser when read.
/// </summary>
internal sealed class ScheduleArchive(
    IDbContextFactory<ShowsDbContext> contextFactory,
    ILogger<ScheduleArchive> logger)
{
    /// <summary>The retained snapshots, newest first — so the first entry is the aging layer 1.</summary>
    public async Task<IReadOnlyList<ScheduleArchiveFile>> ListNewestFirstAsync(CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var dates = await db.DiscoveryScheduleArchives
            .AsNoTracking()
            .OrderByDescending(entry => entry.FetchedOn)
            .Select(entry => entry.FetchedOn)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return dates.Select(date => new ScheduleArchiveFile(date)).ToList();
    }

    /// <summary>
    /// Read one snapshot's raw body. A missing row is logged and skipped; database failures are allowed
    /// to surface because they indicate the archive could not be queried reliably.
    /// </summary>
    public async Task<string?> ReadJsonAsync(ScheduleArchiveFile file, CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var json = await db.DiscoveryScheduleArchives
            .AsNoTracking()
            .Where(entry => entry.FetchedOn == file.FetchedOn)
            .Select(entry => entry.RawJson)
            .SingleOrDefaultAsync(ct)
            .ConfigureAwait(false);

        if (json is null)
        {
            logger.LogWarning(
                "Discovery archive {Date} could not be found; querying without that layer.",
                file.FetchedOn);
        }

        return json;
    }

    /// <summary>
    /// Store the current response only when the aging layer has earned promotion: no archive yet, or
    /// the newest one has reached <paramref name="layerDays"/>. A same-day re-fetch replaces today's
    /// body in place. The SQLite upsert makes that replacement safe across concurrent app instances.
    /// </summary>
    public async Task ArchiveIfAgedAsync(
        string json, DateOnly today, int layerDays, CancellationToken ct)
    {
        var files = await ListNewestFirstAsync(ct).ConfigureAwait(false);
        var newest = files.Count > 0 ? files[0] : null;
        var aged = newest is null || newest.FetchedOn <= today.AddDays(-Math.Max(1, layerDays));
        var isToday = newest?.FetchedOn == today;
        if (!aged && !isToday)
        {
            return;
        }

        var date = today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        await using var db = await contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO discovery_schedule_archive (fetched_on, raw_json) VALUES ({date}, {json}) ON CONFLICT(fetched_on) DO UPDATE SET raw_json = excluded.raw_json",
            ct).ConfigureAwait(false);

        logger.LogInformation(
            "Discovery snapshot archived for {Date} ({Size:N0} bytes); {Layers} layer(s) retained.",
            today, System.Text.Encoding.UTF8.GetByteCount(json), isToday ? files.Count : files.Count + 1);
    }

    /// <summary>
    /// Delete captures older than <paramref name="maxAgeDays"/>. A non-positive maximum keeps
    /// everything (pruning disabled).
    /// </summary>
    public async Task PruneAsync(DateOnly today, int maxAgeDays, CancellationToken ct)
    {
        if (maxAgeDays <= 0)
        {
            return;
        }

        var oldestKept = today.AddDays(-maxAgeDays);
        await using var db = await contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var deleted = await db.DiscoveryScheduleArchives
            .Where(entry => entry.FetchedOn < oldestKept)
            .ExecuteDeleteAsync(ct)
            .ConfigureAwait(false);

        if (deleted > 0)
        {
            logger.LogInformation(
                "Discovery archive dropped {Layers} layer(s) older than the {MaxAge}-day limit.",
                deleted, maxAgeDays);
        }
    }
}
