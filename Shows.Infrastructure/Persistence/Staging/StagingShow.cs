namespace Shows.Infrastructure.Persistence.Staging;

/// <summary>
/// The myshows.me source layer (SCHEMA.md §5). Populated by <c>--stage</c> and the scraper, edited by
/// the user in DB Browser, read by <c>--report</c> and <c>--promote</c>. Short-lived by design — once
/// promoted, <c>myshows_id</c> has served its purpose and does not reach the result schema (ADR-019).
/// </summary>
/// <remarks>
/// Deliberately carries <b>no</b> <c>status</c> column: the export's <c>"Returning Series"</c> is
/// myshows vocabulary and must not contaminate the result schema (ADR-022). TVmaze's
/// <c>tvmaze_show.status_raw</c> is the only status that maps (SPEC R12).
/// </remarks>
public sealed class StagingShow
{
    /// <summary>The export's <c>show.id</c> — a MYSHOWS id, not TVmaze (DATA_ANALYSIS.md §3).</summary>
    public int MyshowsId { get; set; }

    /// <summary>Export <c>title</c>, TRIMMED — the Russian name (ADR-015).</summary>
    public string? TitleRu { get; set; }

    /// <summary>Export <c>titleOriginal</c>.</summary>
    public string? TitleOriginal { get; set; }

    public int? Year { get; set; }
    public string? Alias { get; set; }

    /// <summary>watching.json | later.json | ...</summary>
    public string SourceFile { get; set; } = string.Empty;

    /// <summary>watching | later | cancelled | finished.</summary>
    public string SourceWatchStatus { get; set; } = string.Empty;

    public int TotalEpisodes { get; set; }
    public int WatchedEpisodes { get; set; }
    public int? TotalSeasons { get; set; }

    /// <summary>From staging_imdb_id.</summary>
    public string? ImdbId { get; set; }

    /// <summary>From /lookup?imdb=.</summary>
    public int? TvmazeId { get; set; }

    /// <summary>'scraped' | 'manual' | 'title_year' | 'unresolved'.</summary>
    public string TvmazeResolution { get; set; } = string.Empty;

    public bool NeedsReview { get; set; }

    /// <summary>Set by --promote; NULL means not yet promoted.</summary>
    public DateTimeOffset? PromotedAt { get; set; }
}
