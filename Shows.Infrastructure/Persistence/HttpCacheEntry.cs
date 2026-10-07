namespace Shows.Infrastructure.Persistence;

/// <summary>
/// Transport-layer HTTP cache for ETag revalidation (SCHEMA.md §2). **Not a source table** — the
/// mapper must never read it; if data is only reachable through here, the ingest step failed.
/// Infrastructure-owned, keyed by URL.
/// </summary>
public sealed class HttpCacheEntry
{
    public string Url { get; set; } = string.Empty;
    public string? Etag { get; set; }
    public string? LastModified { get; set; }
    public string Body { get; set; } = string.Empty;
    public DateTimeOffset FetchedAt { get; set; }
}
