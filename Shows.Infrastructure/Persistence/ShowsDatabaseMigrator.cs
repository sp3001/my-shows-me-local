using Microsoft.EntityFrameworkCore;

namespace Shows.Infrastructure.Persistence;

/// <summary>
/// Applies EF migrations only when no writer lease is live (ADR-028, <c>IMPLEMENTATION_PLAN.md</c>
/// step 1.6). A migration that rewrites a table while a bulk job is mid-transaction corrupts that
/// job's work, so the gate refuses rather than racing it.
/// </summary>
/// <remarks>
/// The check is a plain read of <c>writer_lease</c> for any row whose <c>expires_at</c> is still in the
/// future — the same liveness test the lease itself uses. A live lease names the job that holds it, and
/// that name is what the exception carries so the app can show "the Enrichment job is running" instead
/// of a stack trace.
/// </remarks>
public sealed class ShowsDatabaseMigrator(IDbContextFactory<ShowsDbContext> contextFactory)
{
    /// <summary>
    /// Migrates the database to the latest schema. Throws <see cref="LiveLeaseException"/> naming the
    /// holding job(s) if any lease is live, leaving the schema untouched.
    /// </summary>
    public async Task MigrateAsync(CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        // On a brand-new database the writer_lease table does not exist yet, so there is nothing to
        // check — the very first migration creates it. Only gate once the table is present.
        if (await WriterLeaseTableExistsAsync(context, cancellationToken))
        {
            // SQLite stores DateTimeOffset as TEXT and cannot translate a DateTimeOffset comparison in
            // LINQ (the same limitation as ORDER BY on AirStamp). The lease's own acquire/renew SQL only
            // ever compares two stored columns for this reason; here we compare the stored expires_at
            // against a parameterised UTC instant, which is lexicographically correct for +00:00 values.
            var now = DateTimeOffset.UtcNow;
            var liveJobs = await context.Database
                .SqlQueryRaw<string>(
                    "SELECT name AS [Value] FROM writer_lease WHERE expires_at >= {0}", now)
                .ToListAsync(cancellationToken);

            if (liveJobs.Count > 0)
            {
                throw new LiveLeaseException(liveJobs);
            }
        }

        await context.Database.MigrateAsync(cancellationToken);
    }

    private static async Task<bool> WriterLeaseTableExistsAsync(
        ShowsDbContext context, CancellationToken cancellationToken)
    {
        // SqlQueryRaw lets EF own the connection's open/close lifecycle. Opening the pooled connection
        // by hand here leaves it in a state that makes the subsequent Migrate() see a readonly database.
        return await context.Database
            .SqlQueryRaw<int>("SELECT 1 AS [Value] FROM sqlite_master WHERE type = 'table' AND name = 'writer_lease'")
            .AnyAsync(cancellationToken);
    }
}

/// <summary>
/// Raised when a migration is attempted while one or more writer leases are live. Carries the lease
/// names so the caller can surface which job is running rather than a stack trace.
/// </summary>
public sealed class LiveLeaseException : InvalidOperationException
{
    public LiveLeaseException()
    {
    }

    public LiveLeaseException(string message) : base(message)
    {
    }

    public LiveLeaseException(string message, Exception innerException) : base(message, innerException)
    {
    }

    public LiveLeaseException(IReadOnlyList<string> liveLeaseNames)
        : base($"Cannot migrate the database while a writer lease is live: {string.Join(", ", liveLeaseNames)}. "
            + "Stop the running job and try again (ADR-028).")
    {
        LiveLeaseNames = liveLeaseNames;
    }

    public IReadOnlyList<string> LiveLeaseNames { get; } = Array.Empty<string>();
}
