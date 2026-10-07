namespace Shows.Infrastructure.Persistence.Sources;

/// <summary>
/// Provenance for each IMDb dataset file (SCHEMA.md §4). Lets a re-import tell a stale dataset from a
/// fresh one without re-downloading.
/// </summary>
public sealed class ImdbDatasetMeta
{
    /// <summary>'title.basics' | 'title.episode' | 'title.akas'.</summary>
    public string DatasetName { get; set; } = string.Empty;

    public DateTimeOffset DownloadedAt { get; set; }
    public string SourceUrl { get; set; } = string.Empty;
    public long? RowCount { get; set; }
    public long? FileSize { get; set; }
    public string? Checksum { get; set; }
}
