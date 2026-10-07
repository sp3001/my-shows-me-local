namespace Shows.Domain;

/// <summary>
/// One episode, as the union of whichever provider sides know about it.
/// </summary>
/// <remarks>
/// <para>
/// The two provider ids are nullable and <b>symmetric</b> (ADR-025): an episode may be known to
/// TVmaze only, to IMDb only, or to both. Treating the TVmaze id as the assumed-present key would
/// encode "TVmaze always has it" and block the IMDb-only episodes that open question 1 is about.
/// </para>
/// <para>
/// <c>SeasonNumber</c> and <c>EpisodeNumber</c> are denormalised onto the row deliberately — episode
/// ordering and the import's backfill both sort by them, and joining through <see cref="Season"/> for
/// every row is pure waste. <c>EpisodeNumber</c> can be null for IMDb legacy rows, where the
/// <c>UNIQUE (show_id, season_number, episode_number)</c> index does not constrain them (SQLite
/// treats nulls as distinct), so the mapper cannot rely on that index to deduplicate.
/// </para>
/// </remarks>
public sealed class Episode
{
    /// <summary>Client-generated surrogate key (SPEC R13). Permanent for the life of the row — <c>episode_watch</c> points at it.</summary>
    public long Id { get; set; }

    public long ShowId { get; set; }
    public long SeasonId { get; set; }

    /// <summary>Null when this episode is IMDb-only. TVmaze's id space is int.</summary>
    public int? TvmazeEpisodeId { get; set; }

    public int SeasonNumber { get; set; }

    /// <summary>Null if genuinely unknown (IMDb legacy).</summary>
    public int? EpisodeNumber { get; set; }

    public string? Title { get; set; }
    public string? TitleLocalized { get; set; }

    public EpisodeType EpisodeType { get; set; }

    /// <summary>Date from TVmaze <c>airdate</c>.</summary>
    public DateOnly? AirDate { get; set; }

    /// <summary>Offset-aware instant from <c>airstamp</c>. The canonical ordering key.</summary>
    public DateTimeOffset? AirStamp { get; set; }

    public int? RuntimeMinutes { get; set; }
    public double? RatingAverage { get; set; }
    public string? ImageUrl { get; set; }
    public string? ImageMediumUrl { get; set; }

    /// <summary>HTML stripped by the mapper.</summary>
    public string? Description { get; set; }

    /// <summary>Null when this episode is TVmaze-only.</summary>
    public string? ImdbId { get; set; }

    /// <summary>Provenance: <c>tvmaze</c> / <c>imdb</c> / <c>manual</c>. Without it you cannot tell a TVmaze gap from an IMDb guess after three job runs.</summary>
    public string? TitleSource { get; set; }

    public string? AirDateSource { get; set; }
}
