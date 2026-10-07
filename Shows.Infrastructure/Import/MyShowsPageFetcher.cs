using System.Net;

namespace Shows.Infrastructure.Import;

/// <summary>
/// Production fetcher over the <c>MyShows</c> named <see cref="HttpClient"/> (created via
/// <see cref="IHttpClientFactory"/>). The named client carries the per-service proxy, throttle and
/// User-Agent (ARCHITECTURE.md §4, SPEC R14) — this class must not build its own client or inspect a URL
/// to decide routing.
/// </summary>
/// <remarks>
/// A transport exception (DNS, reset, timeout — the shape TVmaze-style blocking takes) is caught and
/// returned as a <see cref="PageFetchResult"/> with a null status and an error string, never thrown, so
/// the scraper records it as a retryable transport failure rather than crashing the run.
/// </remarks>
internal sealed class MyShowsPageFetcher(IHttpClientFactory httpClientFactory) : IMyShowsPageFetcher
{
    public async Task<PageFetchResult> FetchAsync(int myshowsId, CancellationToken cancellationToken)
    {
        var client = httpClientFactory.CreateClient("MyShows");
        try
        {
            // Relative to the named client's BaseAddress (https://myshows.me). Trailing slash matches the
            // verified URL shape /view/{id}/.
            var uri = new Uri($"view/{myshowsId}/", UriKind.Relative);
            using var response = await client.GetAsync(uri, cancellationToken);
            if (response.StatusCode != HttpStatusCode.OK)
            {
                return new PageFetchResult((int)response.StatusCode, null, $"HTTP {(int)response.StatusCode}");
            }

            var html = await response.Content.ReadAsStringAsync(cancellationToken);
            return new PageFetchResult((int)response.StatusCode, html, null);
        }
        catch (HttpRequestException ex)
        {
            // Transport-level failure (no response reached us). Distinct from a 200-with-no-id page.
            return new PageFetchResult(null, null, $"transport: {ex.GetType().Name}: {ex.Message}");
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            // HttpClient surfaces a timeout as TaskCanceledException with no inner cancellation.
            return new PageFetchResult(null, null, $"timeout: {ex.Message}");
        }
    }
}
