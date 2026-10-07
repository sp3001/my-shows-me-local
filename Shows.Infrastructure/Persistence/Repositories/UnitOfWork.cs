using Shows.Application;

namespace Shows.Infrastructure.Persistence.Repositories;

/// <summary>
/// EF implementation of <see cref="IUnitOfWork"/>. Commits the pending changes the repositories have
/// staged on the shared context.
/// </summary>
/// <remarks>
/// Retry rule (SPEC R13, ADR-027): a constraint violation retries the <em>whole</em> unit of work with
/// fresh ids, never the failed statement. That is the caller's responsibility — this method just
/// commits and surfaces the exception; it never resumes a failed transaction.
/// </remarks>
public sealed class UnitOfWork(ShowsDbContext context) : IUnitOfWork
{
    public Task<int> SaveChangesAsync(CancellationToken cancellationToken) =>
        context.SaveChangesAsync(cancellationToken);
}
