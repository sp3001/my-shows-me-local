namespace Shows.Application;

/// <summary>
/// Commits the unit of work.
/// </summary>
/// <remarks>
/// <b>Retry rule (SPEC R13, ADR-027):</b> a constraint violation retries the <em>whole</em> unit of
/// work with fresh ids, never the failed statement. Every writer here is an idempotent upsert keyed on
/// natural keys, which is what makes retry safe at all — resuming a failed transaction would reuse ids
/// that may already be committed.
/// </remarks>
public interface IUnitOfWork
{
    Task<int> SaveChangesAsync(CancellationToken cancellationToken);
}
