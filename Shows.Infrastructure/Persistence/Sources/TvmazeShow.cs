namespace Shows.Infrastructure.Persistence.Sources;

/// <summary>
/// The TVmaze show payload, stored verbatim (SCHEMA.md §3). Column names mirror the API keys so a
/// payload change is visible in a diff. Written by the ingest step, read <b>only</b> by the mapper
/// (SPEC P3/R11). Not a Domain type — the application layer must never see a source table.
/// </summary>
public sealed class TvmazeShow
{
    /// <summary>The API <c>id</c> — the natural key and PK.</summary>
    public int TvmazeId { get; set; }

    public string? Url { get; set; }
    public string? Name { get; set; }

    /// <summary>VERBATIM: 'Scripted' | 'Animated' | ...</summary>
    public string? TypeRaw { get; set; }

    public string? Language { get; set; }

    /// <summary>VERBATIM: 'Running' | 'Ended' | ... The raw string is kept for re-mapping and forensics.</summary>
    public string StatusRaw { get; set; } = string.Empty;

    public int? RuntimeMinutes { get; set; }

    /// <summary>'averageRuntime' — usually the usable one when runtime is null (Black Mirror case).</summary>
    public int? AverageRuntime { get; set; }

    public string? Premiered { get; set; }
    public string? Ended { get; set; }
    public string? OfficialSite { get; set; }
    public string? ScheduleTime { get; set; }
    public string? ScheduleDays { get; set; }
    public double? RatingAverage { get; set; }
    public string? NetworkName { get; set; }
    public string? WebchannelName { get; set; }

    /// <summary>
    /// VERBATIM: <c>network.country.name</c> when a broadcast network carries one, else
    /// <c>webChannel.country.name</c>. Null when the broadcaster has no country object — the common case
    /// for streaming, where the payload reads <c>"webChannel": { "name": "Netflix", "country": null }</c>.
    /// The language fallback is a <b>display</b> rule (<c>CountryRules</c>) applied by the mapper, never
    /// written here: the source table holds the payload, not our interpretation of it (SPEC R11).
    /// </summary>
    public string? CountryName { get; set; }

    /// <summary>VERBATIM: the two-letter <c>country.code</c> from the same nested country object.</summary>
    public string? CountryCode { get; set; }
    public string? ImdbId { get; set; }
    public int? ThetvdbId { get; set; }
    public int? TvrageId { get; set; }

    /// <summary>JSON array, verbatim.</summary>
    public string? GenresRaw { get; set; }

    public string? ImageOriginal { get; set; }
    public string? ImageMedium { get; set; }

    /// <summary>RAW HTML, not stripped — stripping is a mapper concern and is irreversible.</summary>
    public string? SummaryHtml { get; set; }

    /// <summary>API <c>updated</c>, unix seconds.</summary>
    public long? ProviderUpdated { get; set; }

    /// <summary>Full payload as fetched — TVmaze adds fields without notice.</summary>
    public string RawJson { get; set; } = string.Empty;

    public DateTimeOffset FetchedAt { get; set; }
}
