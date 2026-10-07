namespace Shows.Application;

/// <summary>
/// Coordination between concurrent bulk writers (ADR-028).
/// </summary>
/// <remarks>
/// <para>
/// The app and the jobs write to the same file at the same time — the user confirmed jobs may run
/// while the app is open. WAL serialises single write transactions but not multi-transaction bulk
/// work, so a bulk operation takes a row in <c>writer_lease</c> and renews it on a heartbeat.
/// </para>
/// <para>
/// The app takes no lease: blocking the UI because a job is running would be worse, and interactive
/// writes are single-row.
/// </para>
/// </remarks>
public interface IWriterLease
{
    /// <summary>
    /// Tries to take the lease. Returns null when it is already held by a live holder — the acquire is
    /// a conditional upsert, so there is no read-then-write race and a live lease is never overwritten.
    /// </summary>
    /// <param name="name">
    /// The lease name, e.g. <c>promote</c>, <c>episode_refresh</c>. <c>promote</c> is additionally
    /// exclusive of every other lease (ADR-028).
    /// </param>
    Task<IWriterLeaseHandle?> TryAcquireAsync(string name, CancellationToken cancellationToken);
}

/// <summary>
/// A held lease. Dispose releases it.
/// </summary>
/// <remarks>
/// <see cref="RenewAsync"/> is the load-bearing member: <b>a renewal that updates zero rows means the
/// lease was lost and the job must stop writing immediately.</b> That is not a warning to log and
/// carry on — a job that keeps writing after losing its lease is two writers in one bulk operation,
/// which is the exact thing the lease exists to prevent. Expiry is the cleanup mechanism, so a killed
/// process leaves a row that goes stale rather than a lock that must be reaped.
/// </remarks>
public interface IWriterLeaseHandle : IAsyncDisposable
{
    string Name { get; }

    /// <summary>False means the lease is gone. Callers must treat false as "stop writing now".</summary>
    Task<bool> RenewAsync(CancellationToken cancellationToken);
}
