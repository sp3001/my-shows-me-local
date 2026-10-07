namespace Shows.Infrastructure.Persistence.Sources;

/// <summary>
/// An IMDb episode row (SCHEMA.md §4). Read only by the mapper. Season/episode numbers are NULL from
/// <c>\N</c>, never 0 — a 0 would silently collide with a real season 0 (specials).
/// </summary>
public sealed class ImdbEpisode
{
    public string Tconst { get; set; } = string.Empty;
    public string ParentTconst { get; set; } = string.Empty;
    public int? SeasonNumber { get; set; }
    public int? EpisodeNumber { get; set; }
    public string? PrimaryTitle { get; set; }
    public int? StartYear { get; set; }
    public int? RuntimeMinutes { get; set; }
}
