namespace Shows.Infrastructure.Persistence.Staging;

/// <summary>
/// An unwatched episode from the myshows export (SCHEMA.md §5). The column is named <c>episode</c> but
/// the semantics are "unwatched episode" (DATA_ANALYSIS.md §4) — documented because getting it
/// backwards inverts the entire import.
/// </summary>
public sealed class StagingEpisode
{
    /// <summary>Composite natural key (SCHEMA.md §5 UNIQUE).</summary>
    public int ShowMyshowsId { get; set; }

    public int SeasonNumber { get; set; }
    public int EpisodeNumber { get; set; }

    /// <summary>The RPC batch id (58 in practice).</summary>
    public int SourceBatchId { get; set; }

    /// <summary>myshows episode id, for traceability.</summary>
    public int EpisodeIdSrc { get; set; }

    public string? Title { get; set; }
    public string? AirDateUtc { get; set; }
    public bool IsSpecial { get; set; }
    public int? RuntimeMinutes { get; set; }
}
