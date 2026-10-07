namespace Shows.Infrastructure.Persistence.Sources;

/// <summary>
/// Every raw TVmaze status string ever observed, with a counter (SCHEMA.md §3). This is how an
/// unrecognised status becomes <b>visible</b> rather than silently landing in <c>Unknown</c>:
/// <c>SELECT * FROM tvmaze_status_seen WHERE mapped_to IS NULL</c> should be empty at all times.
/// </summary>
public sealed class TvmazeStatusSeen
{
    /// <summary>PK — a repeated string cannot double-count; the counter is incremented instead.</summary>
    public string StatusRaw { get; set; } = string.Empty;

    public DateTimeOffset FirstSeenAt { get; set; }
    public DateTimeOffset LastSeenAt { get; set; }
    public int Occurrences { get; set; }

    /// <summary>The ShowStatus it resolved to; NULL = unmapped (SPEC R12 dictionary needs an edit).</summary>
    public int? MappedTo { get; set; }
}
