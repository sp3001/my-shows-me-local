namespace Shows.Application;

/// <summary>
/// The three Discovery buckets (user-issues 19). Each is derived from the parsed TVmaze
/// <c>/schedule/full</c> snapshot, not from a live endpoint call — the tab is driven by one cached
/// schedule rather than the non-existent <c>/newshows</c> / <c>/upcoming</c> endpoints (issue 1).
/// </summary>
/// <remarks>
/// The bucket definitions are owned by <c>SPEC.md</c> (requirements); this enum only names them for the
/// read seam. The window that separates "New"/"Airing" from older shows is configuration
/// (<c>ARCHITECTURE.md</c> §4), applied by the source, not baked in here.
/// </remarks>
public enum DiscoveryBucket
{
    /// <summary>Any season 1 episode airing after <c>now - window</c> (a first season, recent or still ahead).</summary>
    New = 0,

    /// <summary>Any episode (any season) airing after <c>now - window</c>.</summary>
    Airing = 1,

    /// <summary>A future-dated season premiere (an episode numbered 1 airing after <c>now</c>).</summary>
    Upcoming = 2,
}

/// <summary>
/// The Discovery filter set (user-issues 19): free-text name plus genre / network / country / type facets.
/// Every field is optional; an empty field matches everything. Matching is case-insensitive substring for
/// name, and case-insensitive substring for the facets too (a partial "HBO" should find "HBO Max").
/// </summary>
/// <param name="ShowType">
/// The TVmaze show <c>type</c> ("Scripted", "Reality", "Documentary", …). The UI defaults this to
/// "Scripted" (user-issues 19 follow-up); the source applies whatever is passed (null = all types).
/// </param>
public sealed record DiscoveryFilter(
    string? Name = null,
    string? Genre = null,
    string? Network = null,
    string? Country = null,
    string? ShowType = null)
{
    /// <summary>
    /// The sentinel a facet field carries to mean "match shows whose value for this facet is UNFILLED"
    /// (user-issues 19 follow-up). It is distinct from <c>null</c>/empty, which means "All" (no filter):
    /// "Not set" is a real filter that selects, say, the shows with no country — not everything. A control
    /// character no TVmaze value can contain, so it never collides with a real selection.
    /// </summary>
    public const string NotSet = "\u0000";

    /// <summary>True when no facet is set — the source then skips all predicate work.</summary>
    public bool IsEmpty =>
        string.IsNullOrWhiteSpace(Name)
        && string.IsNullOrWhiteSpace(Genre)
        && string.IsNullOrWhiteSpace(Network)
        && string.IsNullOrWhiteSpace(Country)
        && string.IsNullOrWhiteSpace(ShowType);
}

/// <summary>
/// One selectable facet value with how many shows in the current bucket carry it (user-issues 19). The UI
/// renders these as dropdown options labelled <c>Value (Count)</c>. An empty <see cref="Value"/> is the
/// synthetic "All" entry (the count is the unfiltered-for-this-facet total). A value equal to
/// <see cref="DiscoveryFilter.NotSet"/> is the synthetic "Not set" entry: it selects the shows whose value
/// for this facet is unfilled, and its count is how many such shows there are.
/// </summary>
public sealed record DiscoveryFacetOption(string Value, int Count);

/// <summary>
/// One page of Discovery results (user-issues 19). Pagination is applied by the source so the UI never
/// materialises the whole schedule; <see cref="TotalItems"/> is the count <em>after</em> filtering, so
/// the pager can compute the page count without a second read.
/// </summary>
/// <remarks>
/// The four facet-option lists let the UI populate its dropdowns from the same read that produced the
/// page — no second query. Each facet's counts respect every <em>other</em> active filter (standard
/// faceted search), so a facet's own selection never zeroes its own option list.
/// </remarks>
public sealed record DiscoveryPage(
    IReadOnlyList<CatalogShow> Items,
    int Page,
    int PageSize,
    int TotalItems,
    IReadOnlyList<DiscoveryFacetOption> GenreOptions,
    IReadOnlyList<DiscoveryFacetOption> NetworkOptions,
    IReadOnlyList<DiscoveryFacetOption> CountryOptions,
    IReadOnlyList<DiscoveryFacetOption> TypeOptions)
{
    public int TotalPages => PageSize <= 0 ? 0 : (int)Math.Ceiling(TotalItems / (double)PageSize);

    public bool HasPrevious => Page > 1;

    public bool HasNext => Page < TotalPages;
}
