namespace Shows.Infrastructure.Persistence.Sources;

/// <summary>
/// An IMDb aka (alternative title) row (SCHEMA.md §4). Source of localized titles for
/// <c>tracked_show.title_localized</c> and <c>episode.title_localized</c>. Read only by the mapper.
/// </summary>
public sealed class ImdbAkas
{
    public string TitleId { get; set; } = string.Empty;
    public int Ordering { get; set; }
    public string? Title { get; set; }
    public string? Region { get; set; }
    public string? Language { get; set; }
    public int? IsOriginalTitle { get; set; }
}
