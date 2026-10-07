namespace Shows.Infrastructure.Import;

/// <summary>
/// The result of fetching one myshows show page. Carries the HTTP status and an error string separately
/// so the scraper can record a <b>transport failure</b> differently from a page that loaded fine but
/// simply has no IMDb id (SPEC R14, step 2.2): a proxy misconfiguration must not be recorded as
/// "no IMDb id on this page".
/// </summary>
/// <param name="HttpStatus">The HTTP status code, or null when the request never got a response.</param>
/// <param name="Html">The page body when the request succeeded (status 200), else null.</param>
/// <param name="Error">A short failure reason for a transport or non-200 failure, else null.</param>
internal sealed record PageFetchResult(int? HttpStatus, string? Html, string? Error);

/// <summary>
/// Fetches one myshows show page. A seam so <see cref="ImdbIdScraper"/>'s resumable/cap/failure logic is
/// testable without the network; the production implementation uses the <c>MyShows</c> named client.
/// </summary>
internal interface IMyShowsPageFetcher
{
    Task<PageFetchResult> FetchAsync(int myshowsId, CancellationToken cancellationToken);
}
