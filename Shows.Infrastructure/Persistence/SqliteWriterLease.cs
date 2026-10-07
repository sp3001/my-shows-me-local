using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Shows.Application;
using Shows.Infrastructure.Configuration;

namespace Shows.Infrastructure.Persistence;

/// <summary>
/// The <c>writer_lease</c> coordination for concurrent bulk writers (ADR-028, SCHEMA.md §2). Acquire is
/// a conditional upsert so there is no read-then-write race and a live lease is never overwritten;
/// expiry is the cleanup mechanism, so a killed process leaves a stale row rather than a lock to reap.
/// </summary>
/// <remarks>
/// Registered as a singleton. Each lease operation opens its own short transaction through the context
/// factory, so it never shares a unit of work with the bulk write it protects.
/// </remarks>
public sealed class SqliteWriterLease : IWriterLease
{
    private readonly IDbContextFactory<ShowsDbContext> _contextFactory;
    private readonly int _lifetimeSeconds;
    private readonly string _holder;

    public SqliteWriterLease(IDbContextFactory<ShowsDbContext> contextFactory, IOptions<ShowsOptions> options)
    {
        _contextFactory = contextFactory;
        _lifetimeSeconds = options.Value.Sync.LeaseLifetimeSeconds;

        // 'pid-<pid>@<launch instant>/<random>' — process, launch instant, and a random suffix so two
        // processes that reuse a pid across a restart are still distinguishable (SCHEMA.md §2).
        _holder = $"pid-{Environment.ProcessId}@{DateTimeOffset.UtcNow:O}/{Guid.NewGuid():N}";
    }

    public async Task<IWriterLeaseHandle?> TryAcquireAsync(string name, CancellationToken cancellationToken)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);

        var now = DateTimeOffset.UtcNow;
        var expiresAt = now.AddSeconds(_lifetimeSeconds);

        // Conditional upsert: insert if absent, or take over only if the existing lease has expired. A
        // live lease (expires_at >= now) matches neither branch, so it is never overwritten.
        var rows = await context.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO writer_lease (name, holder, acquired_at, expires_at)
            VALUES ({0}, {1}, {2}, {3})
            ON CONFLICT(name) DO UPDATE
              SET holder = excluded.holder, acquired_at = excluded.acquired_at, expires_at = excluded.expires_at
            WHERE writer_lease.expires_at < excluded.acquired_at;
            """,
            [name, _holder, now, expiresAt], cancellationToken);

        return rows > 0 ? new Handle(this, name) : null;
    }

    // Renew: extend expires_at only while we are still the holder. Zero rows means the lease was lost
    // (taken over after expiry, or released) — the caller must stop writing immediately (ADR-028).
    internal async Task<bool> RenewAsync(string name, CancellationToken cancellationToken)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var rows = await context.Database.ExecuteSqlRawAsync(
            "UPDATE writer_lease SET expires_at = {1} WHERE name = {0} AND holder = {2};",
            [name, DateTimeOffset.UtcNow.AddSeconds(_lifetimeSeconds), _holder], cancellationToken);
        return rows > 0;
    }

    internal async ValueTask ReleaseAsync(string name, CancellationToken cancellationToken)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        // Release only our own lease — never one another process has since taken.
        await context.Database.ExecuteSqlRawAsync(
            "DELETE FROM writer_lease WHERE name = {0} AND holder = {1};",
            [name, _holder], cancellationToken);
    }

    private sealed class Handle(SqliteWriterLease owner, string name) : IWriterLeaseHandle
    {
        public string Name { get; } = name;

        public Task<bool> RenewAsync(CancellationToken cancellationToken) =>
            owner.RenewAsync(Name, cancellationToken);

        public ValueTask DisposeAsync() => owner.ReleaseAsync(Name, CancellationToken.None);
    }
}
