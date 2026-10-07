namespace Shows.Infrastructure.Persistence;

/// <summary>
/// One row per job run (SCHEMA.md §2). The only way to answer "did this job actually run, and what
/// did it change?" without reading logs. Infrastructure-owned bookkeeping, not a domain concept.
/// </summary>
public sealed class JobRun
{
    public long Id { get; set; }
    public string JobName { get; set; } = string.Empty;
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public bool Succeeded { get; set; }
    public int? ItemsTotal { get; set; }
    public int? ItemsChanged { get; set; }
    public string? Detail { get; set; }
}
