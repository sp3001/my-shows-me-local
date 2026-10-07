using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Shows.Infrastructure.Configuration;
using Shows.Infrastructure.Persistence;

namespace Shows.Infrastructure.Tvmaze;

/// <summary>
/// The transport-layer <c>http_cache</c> store for ETag/Last-Modified revalidation (SCHEMA.md §2,
/// ARCHITECTURE.md §5). Scoped with the EF context it reads and writes.
/// </summary>
/// <remarks>
/// <para>
/// <b>Revalidation, not freshness.</b> A cached entry is normally revalidated with a conditional GET
/// (<c>If-None-Match</c>/<c>If-Modified-Since</c>); a 304 reuses the stored body, a 200 replaces it.
/// There is no TTL for a validated entry — TVmaze is authoritative and a 304 is one cheap round-trip, so
/// correctness beats saving a request. This is the property step 3.1 asserts: a cached response is
/// revalidated, not refetched.
/// </para>
/// <para>
/// <b>The one exception: a response with no validator.</b> Some responses carry neither an ETag nor a
/// Last-Modified, so there is nothing to revalidate against and every read would refetch the whole body.
/// For those a time-based fallback applies (user-issues improvement 16): a validator-less cached body is
/// reused for up to <see cref="CacheOptions.NoValidatorCacheMaxAgeHours"/> after it was fetched, then
/// refetched. A response that DOES carry a validator is unaffected — it keeps the always-revalidate path.
/// </para>
/// <para>
/// <b>Not a source table.</b> Only this class touches <c>http_cache</c>; the mapper must never read it
/// (SCHEMA.md §2). If data is reachable only through here, the ingest step failed.
/// </para>
/// </remarks>
internal sealed class HttpCacheStore(
    ShowsDbContext db,
    ILogger<HttpCacheStore> logger,
    IOptions<ShowsOptions> options)
{
    /// <summary>
    /// Issue a conditional GET for <paramref name="relativeUrl"/> (resolved against the client's base
    /// address). Attaches <c>If-None-Match</c>/<c>If-Modified-Since</c> from any cached entry; a 304
    /// reuses the stored body, a 200 replaces it. A cached entry that carries no validator is instead
    /// reused directly while it is younger than the configured max age (improvement 16). A non-2xx/304 is
    /// returned with its status so the provider applies R14. A transport failure throws in the client and
    /// propagates — it is never a "no data" result.
    /// </summary>
    public async Task<CacheFetchResult> GetOrCreateAsync(
        HttpClient client,
        string relativeUrl,
        CancellationToken ct)
    {
        var cached = await db.HttpCache.AsNoTracking().FirstOrDefaultAsync(h => h.Url == relativeUrl, ct);

        // Improvement 16: a validator-less cached body has nothing to revalidate against, so reuse it
        // directly until it ages out rather than refetching the full body on every read. A validated
        // entry (ETag or Last-Modified present) falls through to the conditional GET below.
        if (cached is not null && !HasValidator(cached) && IsWithinMaxAge(cached))
        {
            logger.LogDebug(
                "http_cache serving {Url} from the time-based cache (no validator, fetched {FetchedAt:O}).",
                relativeUrl, cached.FetchedAt);
            return new CacheFetchResult(true, cached.Body, Revalidated: false, FromTimeCache: true);
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(relativeUrl, UriKind.Relative));
        if (cached?.Etag is { Length: > 0 } etag)
        {
            request.Headers.TryAddWithoutValidation("If-None-Match", etag);
        }
        else if (cached?.LastModified is { Length: > 0 } lastModified)
        {
            request.Headers.TryAddWithoutValidation("If-Modified-Since", lastModified);
        }

        var response = await client.SendAsync(request, ct);

        if (response.StatusCode == System.Net.HttpStatusCode.NotModified && cached is not null)
        {
            logger.LogDebug("http_cache 304 for {Url}; reusing stored body.", relativeUrl);
            return new CacheFetchResult(true, cached.Body, Revalidated: true);
        }

        if (!response.IsSuccessStatusCode)
        {
            return new CacheFetchResult(false, null, Revalidated: false, response.StatusCode);
        }

        var body = await response.Content.ReadAsStringAsync(ct);
        var newEtag = response.Headers.ETag?.Tag;
        var newLastModified = response.Headers.TryGetValues("Last-Modified", out var lmValues)
            ? lmValues.FirstOrDefault()
            : null;

        await UpsertAsync(relativeUrl, newEtag, newLastModified, body, ct);
        return new CacheFetchResult(true, body, Revalidated: false);
    }

    private static bool HasValidator(HttpCacheEntry cached) =>
        cached.Etag is { Length: > 0 } || cached.LastModified is { Length: > 0 };

    private bool IsWithinMaxAge(HttpCacheEntry cached)
    {
        var maxAgeHours = options.Value.Cache.NoValidatorCacheMaxAgeHours;
        // A non-positive setting disables the time-based reuse entirely (always refetch a validator-less
        // entry), which is the pre-improvement-16 behavior.
        if (maxAgeHours <= 0)
        {
            return false;
        }

        var age = DateTimeOffset.UtcNow - cached.FetchedAt;
        return age < TimeSpan.FromHours(maxAgeHours);
    }

    private async Task UpsertAsync(string url, string? etag, string? lastModified, string body, CancellationToken ct)
    {
        var row = await db.HttpCache.FirstOrDefaultAsync(h => h.Url == url, ct);
        if (row is null)
        {
            row = new HttpCacheEntry { Url = url };
            db.HttpCache.Add(row);
        }

        row.Etag = etag;
        row.LastModified = lastModified;
        row.Body = body;
        row.FetchedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
    }
}

/// <summary>
/// Outcome of one cache-aware fetch. <see cref="Revalidated"/> is true when a 304 reused the stored
/// body (the step-3.1 assertion). <see cref="FromTimeCache"/> is true when a validator-less entry was
/// served straight from the time-based cache with no network call at all (improvement 16).
/// <see cref="Status"/> is set only for a non-success response the provider must classify (R14).
/// </summary>
internal sealed record CacheFetchResult(
    bool Success,
    string? Body,
    bool Revalidated,
    System.Net.HttpStatusCode? Status = null,
    bool FromTimeCache = false);
