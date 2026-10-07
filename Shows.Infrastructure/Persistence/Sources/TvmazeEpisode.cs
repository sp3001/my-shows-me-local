namespace Shows.Infrastructure.Persistence.Sources;

/// <summary>
/// The TVmaze episode payload, stored verbatim (SCHEMA.md §3). Read only by the mapper.
/// </summary>
public sealed class TvmazeEpisode
{
    /// <summary>The API <c>id</c> — the natural key and PK.</summary>
    public int TvmazeEpisodeId { get; set; }

    public int TvmazeShowId { get; set; }

    public string? Url { get; set; }
    public string? Name { get; set; }
    public int SeasonNumber { get; set; }

    /// <summary>API <c>number</c> — null for specials with no number.</summary>
    public int? Number { get; set; }

    /// <summary>VERBATIM: 'regular' | 'special'. Maps to EpisodeType in the mapper, never here.</summary>
    public string TypeRaw { get; set; } = string.Empty;

    public string? Airdate { get; set; }

    /// <summary>Offset-aware instant; the canonical ordering key. Neither derived from nor deriving airdate.</summary>
    public string? Airstamp { get; set; }

    public int? RuntimeMinutes { get; set; }
    public double? RatingAverage { get; set; }
    public string? ImageOriginal { get; set; }
    public string? ImageMedium { get; set; }

    /// <summary>RAW HTML.</summary>
    public string? SummaryHtml { get; set; }

    public string RawJson { get; set; } = string.Empty;
    public DateTimeOffset FetchedAt { get; set; }
}
