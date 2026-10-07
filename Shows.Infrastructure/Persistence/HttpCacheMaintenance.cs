using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Shows.Infrastructure.Configuration;

namespace Shows.Infrastructure.Persistence;

/// <summary>
/// Startup housekeeping for the transport-layer <c>http_cache</c> table.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a purge at all.</b> A cached response that carries no ETag/Last-Modified is reused only until it
/// ages past <see cref="CacheOptions.NoValidatorCacheMaxAgeHours"/> (improvement 16); after that it is
/// never served again — the next read refetches and overwrites it, or the URL is never requested again and
/// the row is dead weight holding a full response body. An entry WITH a validator is never purged: it stays
/// useful for revalidation indefinitely.
/// </para>
/// <para>
/// <b>Why at startup, not a job.</b> The rows are pure transport cache (SCHEMA §2 — read by nothing but the
/// cache store), so reclaiming the space is a cheap opportunistic cleanup, not a data operation that needs
/// a lease, a job_run row, or resumability. One <c>DELETE</c> at app start, right after migration (when no
/// writer lease is live), is enough; a dedicated job would be over-engineering.
/// </para>
/// </remarks>
public sealed class HttpCacheMaintenance(
    IDbContextFactory<ShowsDbContext> contextFactory,
    IOptions<ShowsOptions> options,
    ILogger<HttpCacheMaintenance> logger)
{
    /// <summary>
    /// Deletes validator-less <c>http_cache</c> rows older than the configured max age in a single
    /// statement, returning how many were removed. A non-positive max age (time-based reuse disabled)
    /// removes every validator-less row, since none of them can ever be served again.
    /// </summary>
    public async Task<int> PurgeExpiredValidatorlessAsync(CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        // SQLite stores DateTimeOffset as TEXT and cannot translate a DateTimeOffset comparison in LINQ
        // (the same limitation the migrator notes). The cutoff is a UTC instant (+00:00), and every stored
        // fetched_at was written from DateTimeOffset.UtcNow (+00:00), so a lexicographic < on the TEXT
        // column is correct — the identical technique the lease-liveness check uses.
        var maxAgeHours = options.Value.Cache.NoValidatorCacheMaxAgeHours;
        var cutoff = DateTimeOffset.UtcNow.AddHours(-Math.Max(0, maxAgeHours));

        var removed = await context.Database.ExecuteSqlRawAsync(
            """
            DELETE FROM http_cache
            WHERE (etag IS NULL OR etag = '')
              AND (last_modified IS NULL OR last_modified = '')
              AND fetched_at < {0}
            """,
            [cutoff],
            cancellationToken);

        if (removed > 0)
        {
            logger.LogInformation("Purged {Removed} expired validator-less http_cache row(s).", removed);
        }

        return removed;
    }
}
