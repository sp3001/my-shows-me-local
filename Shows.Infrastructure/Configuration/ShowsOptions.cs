namespace Shows.Infrastructure.Configuration;

/// <summary>
/// The bound shape of <c>appsettings.json</c>. The keys and their meaning are owned by
/// <c>ARCHITECTURE.md</c> §4; this type is that section's only reader, and nothing else in the
/// solution reads these keys directly.
/// </summary>
/// <remarks>
/// <b>Proxy and Http are read in one place</b> — the handler configuration inside
/// <see cref="ShowsInfrastructureServiceCollectionExtensions"/>. No job, provider or scraper reads
/// them; a second reader is how per-service routing quietly becomes per-caller routing
/// (<c>ARCHITECTURE.md</c> §4).
/// </remarks>
public sealed class ShowsOptions
{
    public const string SectionName = "Shows";

    public DataOptions Data { get; set; } = new();
    public ServiceHttpOptions Tvmaze { get; set; } = new();
    public ServiceHttpOptions MyShows { get; set; } = new();
    public ServiceHttpOptions Imdb { get; set; } = new();
    public SyncOptions Sync { get; set; } = new();
    public ProxyOptions Proxy { get; set; } = new();
    public HttpTransportOptions Http { get; set; } = new();
    public CacheOptions Cache { get; set; } = new();
    public LocalizationOptions Localization { get; set; } = new();
    public DiscoveryOptions Discovery { get; set; } = new();
    public LinkOptions Links { get; set; } = new();

    /// <summary>
    /// The per-service settings for one named client. The client name is the key for both the service
    /// options and its proxy, so a caller cannot pair one service's throttle with another's proxy.
    /// </summary>
    public ServiceHttpOptions ForClient(string clientName) => clientName switch
    {
        "Tvmaze" => Tvmaze,
        "MyShows" => MyShows,
        "Imdb" => Imdb,
        _ => throw new ArgumentException($"Unknown named client '{clientName}'.", nameof(clientName)),
    };
}

/// <summary>
/// Where the app's own files live. Everything is relative to <see cref="BaseDirectory"/> so the whole
/// app is relocatable and the Docker migration is a volume mount rather than a code change.
/// </summary>
public sealed class DataOptions
{
    /// <summary>
    /// The base directory that <see cref="DatabasePath"/>, <see cref="ImdbDatasetPath"/> and
    /// <c>log/</c> are resolved against.
    /// </summary>
    /// <remarks>
    /// Empty means "discover it": walk up from the assembly directory for a directory containing
    /// <c>Shows.sln</c>, which is what makes the paths land inside the repo during development
    /// (<c>ARCHITECTURE.md</c> §7 says so explicitly) instead of under <c>bin/</c>. In a container the
    /// key is set to the volume mount and the discovery never runs.
    /// </remarks>
    public string BaseDirectory { get; set; } = string.Empty;

    public string DatabasePath { get; set; } = "data/shows.db";
    public string ImdbDatasetPath { get; set; } = "data/imdb";

    /// <summary>
    /// Where <c>--report</c> writes the <c>v_staging_issues</c> CSV. Empty means <c>data/staging_issues.csv</c>
    /// — inside the data folder, so the report sits beside the database it describes.
    /// </summary>
    public string ReportPath { get; set; } = "data/staging_issues.csv";

    /// <summary>
    /// The directory holding the read-only myshows exports. Empty means the base directory (the repo
    /// root in development, where the inputs sit beside <c>Shows.sln</c>).
    /// </summary>
    public string InputDirectory { get; set; } = string.Empty;
}

/// <summary>Per-service HTTP settings. One instance per named client.</summary>
public sealed class ServiceHttpOptions
{
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>Versioned, and sent on every request (ARCHITECTURE.md §5).</summary>
    public string UserAgent { get; set; } = "ShowsTracker/1.0 (personal use)";

    /// <summary>
    /// Enforced centrally in the HTTP handler, not per call site — a job that loops and forgets
    /// throttling is the main risk to both external services.
    /// </summary>
    public int MinRequestIntervalMs { get; set; } = 250;

    /// <summary>Hard cap for one-off jobs. 0 means uncapped.</summary>
    public int MaxRequestsPerRun { get; set; }
}

/// <summary>Writer-lease timings (ADR-028).</summary>
public sealed class SyncOptions
{
    public int LeaseLifetimeSeconds { get; set; } = 90;
    public int LeaseRenewIntervalSeconds { get; set; } = 30;
}

/// <summary>
/// Localized-title policy (step 7.5). The target country whose <c>/akas</c> title becomes
/// <c>tracked_show.title_localized</c>. This is configuration, not a provider constant — the provider
/// returns raw akas and the pick happens above it (SPEC step 7.5). Default <c>RU</c> for this user; an
/// unrecognized/absent match leaves the localized title null rather than guessing another country.
/// </summary>
public sealed class LocalizationOptions
{
    public string TargetCountryCode { get; set; } = "RU";
}

/// <summary>
/// Discovery tab configuration (user-issues 19). The Discovery feeds are derived from the parsed TVmaze
/// <c>/schedule/full</c> snapshot, so the knobs that shape them live here rather than in code.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="NewShowsWindowDays"/> is the look-back window that separates the New / Airing buckets from
/// older shows: a show whose season 1 (New) or any episode (Airing) airs after <c>now - window</c> is
/// "new"/"airing". <see cref="PageSize"/> is how many shows a Discovery
/// page renders before the pager advances. Both are configuration, not constants, per the issue.
/// </para>
/// <para>
/// <see cref="ScheduleSourcePath"/> points at a local <c>schedule-full.json</c> to parse instead of
/// fetching the live endpoint. The user saved a sample to the repo root for exactly this; while it is
/// set, the source reads that file (handy offline, and TVmaze is blocked from the dev machine —
/// DATA_ANALYSIS §9). When empty, the source fetches <c>/schedule/full</c> through the Tvmaze client and
/// refreshes at most once per <see cref="RefreshHours"/>.
/// </para>
/// </remarks>
public sealed class DiscoveryOptions
{
    /// <summary>Look-back window, in days, for the New and Airing buckets. Default one month.</summary>
    public int NewShowsWindowDays { get; set; } = 30;

    /// <summary>
    /// How long a raw <c>/schedule/full</c> capture is kept as the aging layer before the next fetch
    /// replaces it (user follow-up, 2026-10-07). <c>/schedule/full</c> is forward-only, so the look-back
    /// window can only be filled from captures taken while those episodes were still in the future; this is
    /// the spacing of those captures. It is an <em>interval</em>, not a count: the newest capture ages in
    /// place, and only once it reaches this age does a fetch start a new one. Values at or below 1 archive
    /// on every refresh.
    /// </summary>
    public int ArchiveLayerDays { get; set; } = 14;

    /// <summary>
    /// The age at which a retained capture is deleted (user follow-up, 2026-10-07). Its own knob rather
    /// than a function of <see cref="NewShowsWindowDays"/>: how far back the buckets look and how much
    /// schedule history is worth keeping on disk are separate decisions, and deriving one from the other
    /// means widening the window silently changes retention. To actually fill a window of
    /// <c>NewShowsWindowDays</c> this must exceed it — the source warns when it does not. Zero or negative
    /// keeps every capture (pruning disabled).
    /// </summary>
    public int ArchiveMaxAgeDays { get; set; } = 60;

    /// <summary>Shows per Discovery page. Default 30 (user-issues 19).</summary>
    public int PageSize { get; set; } = 30;

    /// <summary>Minimum hours between live <c>/schedule/full</c> refreshes. The feed is refreshed daily upstream.</summary>
    public int RefreshHours { get; set; } = 24;

    /// <summary>
    /// Absolute or base-relative path to a saved <c>schedule-full.json</c> to parse instead of fetching.
    /// Empty means fetch the live endpoint. Resolved against the base directory when relative.
    /// </summary>
    public string ScheduleSourcePath { get; set; } = string.Empty;
}

/// <summary>
/// Per-service proxy routes (SPEC R14). The keys are the named <c>IHttpClientFactory</c> clients —
/// <c>Tvmaze</c>, <c>MyShows</c>, <c>Imdb</c> — not URL patterns.
/// </summary>
/// <remarks>
/// <para>
/// <b>An empty string means direct, explicitly.</b> It does not mean "inherit the machine's settings":
/// <c>DATA_ANALYSIS.md</c> §9.5 records that ambient settings resolve to no proxy on the development
/// machine and behave identically to explicit direct, so inheriting them would silently do nothing.
/// An absent key is treated as empty, never as ambient.
/// </para>
/// <para>
/// Three separate keys because the hosts are blocked and reachable independently — TVmaze is
/// unreachable directly from the development machine while myshows.me and the IMDb dataset host are
/// fine. One global switch would either leave TVmaze broken or push working traffic through a proxy
/// for nothing.
/// </para>
/// <para>
/// <see cref="Images"/> is the exception to the "named client" framing: it routes the Avalonia head's
/// poster loader (<c>ImageBehavior</c>), which is not an <c>IHttpClientFactory</c> client. The poster
/// host (TVmaze image CDN) is blocked from the development machine just like the API, so it needs its
/// own route; it is read directly by the composition root, not through <see cref="ForClient"/>.
/// </para>
/// </remarks>
public sealed class ProxyOptions
{
    public string Tvmaze { get; set; } = string.Empty;
    public string MyShows { get; set; } = string.Empty;
    public string Imdb { get; set; } = string.Empty;

    /// <summary>
    /// Proxy for the Avalonia poster loader (<c>ImageBehavior</c>). Not a named factory client, so it is
    /// read directly by the composition root rather than through <see cref="ForClient"/>. Empty = direct.
    /// </summary>
    public string Images { get; set; } = string.Empty;

    public string ForClient(string clientName) => clientName switch
    {
        "Tvmaze" => Tvmaze,
        "MyShows" => MyShows,
        "Imdb" => Imdb,
        _ => throw new ArgumentException($"Unknown named client '{clientName}'.", nameof(clientName)),
    };
}

/// <summary>
/// Transport timings, shared by every named client.
/// </summary>
/// <remarks>
/// <see cref="ConnectTimeoutSeconds"/> exists specifically for R14: a <em>refused</em> proxy port fails
/// in about two seconds and names itself, but a <em>filtered</em> one waits out the whole request
/// timeout and looks identical to a slow provider — and identical to TVmaze itself when blocked
/// (<c>DATA_ANALYSIS.md</c> §9.6). Keeping it well below <see cref="RequestTimeoutSeconds"/> is what
/// makes the two distinguishable at all.
/// </remarks>
public sealed class HttpTransportOptions
{
    public int ConnectTimeoutSeconds { get; set; } = 10;
    public int RequestTimeoutSeconds { get; set; } = 60;
}

/// <summary>
/// HTTP cache policy (user-issues improvement 16). Kept separate from <see cref="HttpTransportOptions"/>
/// because that type is read in exactly one place — the handler wiring (ARCHITECTURE.md §4) — and this is
/// a cache-store concern, not a transport timing.
/// </summary>
/// <remarks>
/// <para>
/// The <c>http_cache</c> store normally revalidates a cached entry with a conditional GET (ETag /
/// Last-Modified). But some responses carry <b>no validator at all</b> — no ETag and no Last-Modified —
/// so there is nothing to revalidate against and every read would refetch the full body. For those, a
/// time-based fallback applies: a cached body with no validator is reused for up to
/// <see cref="NoValidatorCacheMaxAgeHours"/> after it was fetched, then refetched. A response that DOES
/// carry a validator is unaffected — it keeps the always-revalidate behavior.
/// </para>
/// </remarks>
public sealed class CacheOptions
{
    /// <summary>
    /// How long a cached response that carries no ETag/Last-Modified may be reused before it is refetched.
    /// Default 24 hours (once per day). Applies ONLY to validator-less responses; a response with a
    /// validator is always revalidated regardless of this value.
    /// </summary>
    public int NoValidatorCacheMaxAgeHours { get; set; } = 24;
}

/// <summary>
/// External-link URL formats (user-issues improvement 20). The URL is built from a show's id at render
/// (SPEC P5 — no URL column is ever stored), and the format lives here rather than as a code constant so
/// the two heads (Avalonia and MAUI) share one source of truth.
/// </summary>
/// <remarks>
/// Each format is a <c>string.Format</c> template with a single <c>{0}</c> placeholder filled with the id:
/// the TVmaze one takes the numeric TVmaze id, the IMDb one takes the <c>tt</c> id string. A blank format
/// disables that link (the button is hidden) rather than producing a broken URL.
/// </remarks>
public sealed class LinkOptions
{
    /// <summary>Format for a TVmaze show page; <c>{0}</c> is the TVmaze id. Default the canonical show URL.</summary>
    public string Tvmaze { get; set; } = "https://www.tvmaze.com/shows/{0}";

    /// <summary>Format for an IMDb title page; <c>{0}</c> is the <c>tt</c> id. Default the canonical title URL.</summary>
    public string Imdb { get; set; } = "https://www.imdb.com/title/{0}/";
}
