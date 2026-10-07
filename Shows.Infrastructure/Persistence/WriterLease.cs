namespace Shows.Infrastructure.Persistence;

/// <summary>
/// Coordination state for concurrent writers (SCHEMA.md §2, ADR-028). Not a source table and not user
/// data — safe to delete when nothing is running. Acquired by a conditional upsert so a live lease is
/// never overwritten; expiry is the cleanup mechanism.
/// </summary>
public sealed class WriterLease
{
    /// <summary>'promote' | 'episode_refresh' | 'status_recheck' | 'enrichment'.</summary>
    public string Name { get; set; } = string.Empty;
    public string Holder { get; set; } = string.Empty;
    public DateTimeOffset AcquiredAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
}
