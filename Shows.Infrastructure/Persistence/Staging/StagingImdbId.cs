namespace Shows.Infrastructure.Persistence.Staging;

/// <summary>
/// The scraped IMDb id per myshows show (SCHEMA.md §5). Resumable: rows with
/// <c>imdb_id IS NULL AND error IS NOT NULL</c> are retried, successful rows are skipped.
/// </summary>
public sealed class StagingImdbId
{
    public int MyshowsId { get; set; }
    public string? ImdbId { get; set; }
    public string? KinopoiskId { get; set; }
    public DateTimeOffset? ScrapedAt { get; set; }
    public int? HttpStatus { get; set; }
    public string? Error { get; set; }
}
