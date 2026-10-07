namespace Shows.Infrastructure.Persistence.Sources;

/// <summary>
/// An IMDb title row, scoped to tracked shows (SCHEMA.md §4, ADR-021). Read only by the mapper.
/// </summary>
public sealed class ImdbTitle
{
    public string Tconst { get; set; } = string.Empty;
    public string TitleType { get; set; } = string.Empty;
    public string PrimaryTitle { get; set; } = string.Empty;
    public string? OriginalTitle { get; set; }
    public bool IsAdult { get; set; }
    public int? StartYear { get; set; }
    public int? EndYear { get; set; }
    public int? RuntimeMinutes { get; set; }

    /// <summary>Comma list, verbatim.</summary>
    public string? Genres { get; set; }
}
