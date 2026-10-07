using System.Globalization;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Shows.Application;
using Shows.Infrastructure.Persistence;

namespace Shows.Infrastructure.Tvmaze;

/// <summary>
/// The TVmaze catalog provider (step 3.1) over the <c>Tvmaze</c> named client. Implements both the
/// Application read seam (<see cref="ICatalogProvider"/>) and the mapper's fetch seam
/// (<see cref="ITvmazeShowSource"/>); registering it wires the mapper's <c>MapShowAsync(int)</c> path.
/// </summary>
/// <remarks>
/// <para>
/// <b>R14 is the governing constraint.</b> A transport failure (TVmaze is unreachable directly from the
/// dev machine — <c>DATA_ANALYSIS.md</c> §9) throws <see cref="TvmazeUnavailableException"/> naming the
/// host; it is never swallowed into an empty list. Only a genuine 404 (a show that does not exist) maps
/// to <c>null</c>, and only a genuine empty result set maps to an empty list.
/// </para>
/// <para>
/// Every request goes through <see cref="HttpCacheStore"/> for ETag revalidation, and the named client
/// already serializes per host and applies the Polly policies (retry on 5xx/408/429 only). This class
/// does no fetching of its own beyond the cache store — it never builds an <see cref="HttpClient"/> or
/// inspects a URL to route (SPEC R14).
/// </para>
/// </remarks>
internal sealed class TvmazeCatalogProvider(
    IHttpClientFactory httpClientFactory,
    HttpCacheStore cache,
    ILogger<TvmazeCatalogProvider> logger) : ICatalogProvider, ITvmazeShowSource
{
    private const string Host = "api.tvmaze.com";

    public async Task<IReadOnlyList<CatalogShow>> SearchAsync(string query, CancellationToken cancellationToken)
    {
        var url = $"search/shows?q={Uri.EscapeDataString(query)}";
        return await FetchListAsync(url, cancellationToken);
    }

    public async Task<CatalogShow?> GetShowAsync(int tvmazeId, CancellationToken cancellationToken)
    {
        var body = await FetchBodyAsync($"shows/{tvmazeId.ToString(CultureInfo.InvariantCulture)}", allowNotFound: true, cancellationToken);
        if (body is null)
        {
            return null;
        }

        using var doc = JsonDocument.Parse(body);
        return TvmazeJsonMapper.MapCatalogShow(doc.RootElement);
    }

    public async Task<IReadOnlyList<EpisodeCatalogInfo>> GetEpisodesAsync(int tvmazeId, CancellationToken cancellationToken)
    {
        var url = $"shows/{tvmazeId.ToString(CultureInfo.InvariantCulture)}/episodes";
        var body = await FetchBodyAsync(url, allowNotFound: true, cancellationToken);
        if (body is null)
        {
            return [];
        }

        using var doc = JsonDocument.Parse(body);
        var list = new List<EpisodeCatalogInfo>();
        foreach (var ep in doc.RootElement.EnumerateArray())
        {
            list.Add(TvmazeJsonMapper.MapEpisodeCatalog(ep));
        }

        return list;
    }

    public async Task<int?> LookupByImdbIdAsync(string imdbId, CancellationToken cancellationToken)
    {
        var url = $"lookup/shows?imdb={Uri.EscapeDataString(imdbId)}";
        var body = await FetchBodyAsync(url, allowNotFound: true, cancellationToken);
        if (body is null)
        {
            return null;
        }

        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.Number
            ? id.GetInt32()
            : null;
    }

    // NOTE: the Discovery feeds (issue 19) are NOT here. They used to be GetNewShowsAsync/GetUpcomingAsync
    // over /schedule/new and /schedule/upcoming; those are replaced by ScheduleDiscoverySource, which
    // queries a cached /schedule/full snapshot rather than hitting a live endpoint per request.

    public async Task<IReadOnlyList<ShowAka>> GetAkasAsync(int tvmazeId, CancellationToken cancellationToken)
    {
        // A genuine 404 (a show with no akas at all) and a genuine empty array both mean "TVmaze has no
        // akas" -> empty list. A transport failure throws TvmazeUnavailableException (R14) via FetchBodyAsync.
        var body = await FetchBodyAsync(
            $"shows/{tvmazeId.ToString(CultureInfo.InvariantCulture)}/akas", allowNotFound: true, cancellationToken);
        if (body is null)
        {
            return [];
        }

        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.ValueKind == JsonValueKind.Array
            ? TvmazeJsonMapper.MapAkas(doc.RootElement)
            : [];
    }

    public async Task<IReadOnlyDictionary<int, long>> GetShowUpdateTimestampsAsync(
        CancellationToken cancellationToken)
    {
        // One call returns the whole { tvmazeId: updated } map for every show TVmaze knows about. The
        // response is large, so it is fetched once (and cached by HttpCacheStore) rather than per show —
        // the caller compares these timestamps against its own stored provider_updated to pick which shows
        // to refresh. allowNotFound: false — a 404 here is not a valid empty result, it is a failure to
        // learn anything (R14), so it throws rather than returning an empty map.
        var body = await FetchBodyAsync("updates/shows", allowNotFound: false, cancellationToken);
        using var doc = JsonDocument.Parse(body!);
        var map = new Dictionary<int, long>();
        foreach (var property in doc.RootElement.EnumerateObject())
        {
            // Keys are the TVmaze ids as strings; values are unix-second update timestamps. A non-numeric
            // id or value is skipped rather than throwing — one malformed key must not lose the whole map.
            if (int.TryParse(property.Name, CultureInfo.InvariantCulture, out var tvmazeId)
                && property.Value.ValueKind == JsonValueKind.Number
                && property.Value.TryGetInt64(out var updated))
            {
                map[tvmazeId] = updated;
            }
        }

        return map;
    }

    public async Task<TvmazePayload?> FetchAsync(int tvmazeId, CancellationToken cancellationToken)
    {
        var showBody = await FetchBodyAsync($"shows/{tvmazeId.ToString(CultureInfo.InvariantCulture)}", allowNotFound: true, cancellationToken);
        if (showBody is null)
        {
            return null;
        }

        // ?specials=1 is load-bearing: TVmaze's /episodes endpoint OMITS specials unless asked. Without
        // it the mapper never sees a show's specials, so they are absent from the episode table and
        // has_unwatched_specials is always false. Specials are fetched so they EXIST as rows (SPEC R4
        // excludes them from progress, and the import backfill never marks them watched — DATA_ANALYSIS
        // §5), but they must be present for the UI to list them and for a later re-map to keep them.
        // The cache keys on this exact relative URL, so it is a distinct entry from the plain /episodes.
        var episodesBody = await FetchBodyAsync($"shows/{tvmazeId.ToString(CultureInfo.InvariantCulture)}/episodes?specials=1", allowNotFound: true, cancellationToken);
        var fetchedAt = DateTimeOffset.UtcNow;

        using var showDoc = JsonDocument.Parse(showBody);
        var show = TvmazeJsonMapper.MapShow(showDoc.RootElement, fetchedAt);

        IReadOnlyList<Persistence.Sources.TvmazeEpisode> episodes = [];
        if (episodesBody is not null)
        {
            using var episodesDoc = JsonDocument.Parse(episodesBody);
            episodes = TvmazeJsonMapper.MapEpisodes(episodesDoc.RootElement, tvmazeId, fetchedAt);
        }

        return new TvmazePayload(show, episodes);
    }

    private async Task<IReadOnlyList<CatalogShow>> FetchListAsync(string url, CancellationToken ct)
    {
        var body = await FetchBodyAsync(url, allowNotFound: false, ct);
        using var doc = JsonDocument.Parse(body!);
        var list = new List<CatalogShow>();
        foreach (var element in doc.RootElement.EnumerateArray())
        {
            // Two array shapes reach here. The search endpoints (search/shows, search/episodes' show side)
            // return a score envelope [{ "score": n, "show": {…} }]; the discovery endpoints
            // (schedule/new, schedule/upcoming) return flat show objects. Unwrap the envelope when present so one path maps both —
            // verified live (LOG step 3.1): search returns the envelope, and mapping it as a flat show
            // throws because the wrapper has no "id".
            var show = element.TryGetProperty("show", out var inner) && inner.ValueKind == JsonValueKind.Object
                ? inner
                : element;
            list.Add(TvmazeJsonMapper.MapCatalogShow(show));
        }

        return list;
    }

    /// <summary>
    /// Fetch a body through the cache. Returns null only for a genuine 404 when
    /// <paramref name="allowNotFound"/>; throws <see cref="TvmazeUnavailableException"/> for a transport
    /// failure or any other non-success status (R14). User cancellation propagates unchanged.
    /// </summary>
    private async Task<string?> FetchBodyAsync(string url, bool allowNotFound, CancellationToken ct)
    {
        var client = httpClientFactory.CreateClient("Tvmaze");

        CacheFetchResult result;
        try
        {
            result = await cache.GetOrCreateAsync(client, url, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // the caller cancelled; not a provider failure
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            // Transport failure — the host could not be reached (or timed out). R14: name the host,
            // never return empty. This is the dead-proxy / blocked-host path.
            logger.LogError(ex, "TVmaze transport failure for {Url} on {Host}.", url, Host);
            throw new TvmazeUnavailableException($"Could not reach {Host} (GET {url}): {ex.Message}", ex);
        }

        if (result.Success && result.Body is not null)
        {
            return result.Body;
        }

        if (result.Status == HttpStatusCode.NotFound && allowNotFound)
        {
            return null;
        }

        var status = result.Status.HasValue
            ? ((int)result.Status.Value).ToString(CultureInfo.InvariantCulture)
            : "unknown";
        logger.LogError("TVmaze returned {Status} for {Url} on {Host}.", status, url, Host);
        throw new TvmazeUnavailableException($"{Host} returned HTTP {status} for GET {url}.");
    }
}
