namespace Shows.Infrastructure.Persistence;

/// <summary>
/// One raw TVmaze schedule capture retained for Discovery look-back (SCHEMA.md §2). Infrastructure
/// cache data, keyed by its capture date rather than a generated id.
/// </summary>
public sealed class DiscoveryScheduleArchiveEntry
{
    public DateOnly FetchedOn { get; set; }
    public string RawJson { get; set; } = string.Empty;
}
