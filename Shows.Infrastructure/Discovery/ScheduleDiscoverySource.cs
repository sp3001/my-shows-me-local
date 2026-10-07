using System.Globalization;
using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Shows.Application;
using Shows.Infrastructure.Configuration;
using Shows.Infrastructure.Tvmaze;

namespace Shows.Infrastructure.Discovery;

/// <summary>
/// The Discovery read seam (user-issues 19) over a cached, parsed <c>/schedule/full</c> snapshot.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a process-wide singleton.</b> The snapshot is a ~14 MB parse that is refreshed at most once a
/// day; parsing it per request (or per DI scope) would be absurd. The cache therefore lives on a
/// singleton, guarded by a lock, while the queries themselves are cheap in-memory passes. The use case
/// that consumes this is scoped (it shares the tracked-show repository's context), but it depends on
/// this singleton — a scoped service may depend on a singleton, never the reverse.
/// </para>
/// <para>
/// <b>Two sources, one cache.</b> When <c>DiscoveryOptions.ScheduleSourcePath</c> is set, the snapshot is
/// parsed from that local file (the user's saved sample — TVmaze is blocked from the dev machine,
/// DATA_ANALYSIS §9). Otherwise the source fetches <c>/schedule/full</c> through the Tvmaze named client
/// and refreshes at most once per <c>RefreshHours</c>. Either way the first successful parse is what
/// answers queries; a refresh failure with a warm cache keeps serving the prior snapshot.
/// </para>
/// <para>
/// <b>Live mode goes through <c>http_cache</c>.</b> The live fetch is a conditional GET via
/// <see cref="HttpCacheStore"/>, exactly like <see cref="TvmazeCatalogProvider"/>, so the response is
/// cached and revalidated (ETag/Last-Modified) rather than refetched whole. Because this source is a
/// process-wide singleton while the cache store is scoped to an EF context, the fetch opens a short DI
/// scope to resolve the store — the same scope-per-operation pattern the UI ViewModels use.
/// </para>
/// <para>
/// <b>The archive gives the window a past.</b> <c>/schedule/full</c> only ever contains today and the
/// future, so a look-back window has nothing below today to find and the New/Airing buckets would fill
/// with far-future premieres instead of recent ones. Each fresh response is therefore retained as a dated
/// <em>raw</em> snapshot (see <see cref="ScheduleArchive"/>) and the layers are folded back in at
/// <see cref="BuildSnapshot"/> time, so an episode that aired last week is still visible because it was
/// captured while it was still ahead. This runs on a refresh, not per query: the merge is a few hundred
/// milliseconds a day rather than on every keystroke.
/// </para>
/// <para>
/// <b>R14.</b> A cold cache that cannot be filled throws <see cref="TvmazeUnavailableException"/> naming
/// the host — never an empty page, which would render as "there are no new shows" and be
/// indistinguishable from success.
/// </para>
/// </remarks>
internal sealed class ScheduleDiscoverySource(
    IHttpClientFactory httpClientFactory,
    IServiceScopeFactory scopeFactory,
    DataPaths paths,
    IOptions<ShowsOptions> options,
    ScheduleArchive archive,
    ILogger<ScheduleDiscoverySource> logger) : IDiscoverySource, IDisposable
{
    private const string Host = "api.tvmaze.com";
    private const string ScheduleUrl = "schedule/full";

    private readonly DiscoveryOptions _discovery = options.Value.Discovery;
    private readonly SemaphoreSlim _gate = new(1, 1);

    // Local-file mode parses one fixed sample the user dropped in. There is no stream of responses to
    // accumulate, and dating that sample "today" would misrepresent its age and poison the layer
    // chronology — so the archive is a live-mode concern only.
    private readonly bool _localFileMode = !string.IsNullOrWhiteSpace(paths.DiscoverySchedulePath);

    private IReadOnlyList<DiscoveryEntry>? _snapshot;
    private DateTimeOffset _lastRefreshUtc = DateTimeOffset.MinValue;

    // Set once after the retention-vs-window mismatch has been reported, so a daily refresh does not
    // repeat the same configuration warning forever. Reset is unnecessary: the value cannot change at
    // runtime, since options are bound once at startup.
    private bool _retentionWarned;

    public async Task<DiscoveryPage> GetPageAsync(
        DiscoveryBucket bucket,
        DiscoveryFilter filter,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        var snapshot = await EnsureSnapshotAsync(cancellationToken).ConfigureAwait(false);

        // The caller may pass pageSize <= 0 to mean "use the configured default" (user-issues 19: page
        // size is a config value). The UI learns the effective size from the returned page, so it never
        // has to name configuration itself.
        if (pageSize <= 0)
        {
            pageSize = _discovery.PageSize;
        }

        // The query is a cheap in-memory pass over the cached snapshot (sub-millisecond for ~10k shows), so
        // it runs INLINE — NOT via Task.Run. A Task.Run would force the caller's `await` to resume on a
        // thread-pool thread, and a UI caller that mutates a bound collection after that await would throw
        // "Call from invalid thread". Running inline keeps a warm-cache read fully synchronous on the
        // caller's thread; only the cold refresh (the file/HTTP read above) ever yields.
        return DiscoveryIndex.Query(
            snapshot, bucket, filter, page, pageSize,
            DateOnly.FromDateTime(DateTime.UtcNow), _discovery.NewShowsWindowDays);
    }

    public bool TryGetCached(int tvmazeId, out CatalogShow show, out IReadOnlyList<CatalogEpisode> episodes)
    {
        // Pure cache read — NEVER EnsureSnapshotAsync (that could fetch). The Discovery list is already on
        // screen, so the snapshot is warm; a cold cache simply reports "not cached" and the caller falls
        // back to a live read. Volatile read publishes the array written by a prior refresh.
        var snapshot = Volatile.Read(ref _snapshot);
        var entry = snapshot?.FirstOrDefault(e => e.TvmazeId == tvmazeId);
        if (entry is null)
        {
            show = null!;
            episodes = [];
            return false;
        }

        show = entry.Show;
        episodes = entry.Episodes
            .Select(ep => new CatalogEpisode(
                ep.Season,
                ep.Number,
                ep.Title,
                ep.AirDate,
                IsSpecial: ep.Season == 0
                    || string.Equals(ep.Type, "special", StringComparison.OrdinalIgnoreCase)))
            .ToList();
        return true;
    }

    private async Task<IReadOnlyList<DiscoveryEntry>> EnsureSnapshotAsync(CancellationToken ct)
    {
        // Fast path: a warm snapshot that is still fresh. Volatile read so the published array is visible.
        var current = Volatile.Read(ref _snapshot);
        if (current is not null && IsFresh())
        {
            return current;
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Re-check under the gate — another caller may have refreshed while we waited.
            current = Volatile.Read(ref _snapshot);
            if (current is not null && IsFresh())
            {
                return current;
            }

            try
            {
                var json = await LoadJsonAsync(ct).ConfigureAwait(false);
                var fresh = ScheduleParser.Parse(json);
                var snapshot = await BuildSnapshotAsync(json, fresh, ct).ConfigureAwait(false);
                Volatile.Write(ref _snapshot, snapshot);
                _lastRefreshUtc = DateTimeOffset.UtcNow;
                logger.LogInformation(
                    "Discovery snapshot refreshed: {Shows} shows ({Current} in the live schedule).",
                    snapshot.Count, fresh.Count);
                return snapshot;
            }
            catch (Exception ex) when (ex is not OperationCanceledException && current is not null)
            {
                // A refresh failed but we have a prior snapshot: serve it rather than failing the read.
                // Logged as a warning — the feature degrades to slightly stale data, it does not break.
                logger.LogWarning(ex, "Discovery refresh failed; serving the previous snapshot.");
                return current;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    // The snapshot a query runs against: the fresh response folded together with the retained older
    // snapshots, so episodes that already aired are still present. Runs only on a refresh (at most once
    // per RefreshHours), which is why re-parsing a handful of 14 MB layers is affordable — per query it
    // would not be. The archive is written first so today's capture is available to the next refresh, and
    // pruned first so the merge never reads a layer the age ceiling has already condemned.
    private async Task<IReadOnlyList<DiscoveryEntry>> BuildSnapshotAsync(
        string json, IReadOnlyList<DiscoveryEntry> fresh, CancellationToken ct)
    {
        if (_localFileMode)
        {
            return fresh;
        }

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var windowDays = _discovery.NewShowsWindowDays;
        var maxAgeDays = _discovery.ArchiveMaxAgeDays;

        // The two knobs are independent by design, which means they can be configured against each other:
        // a ceiling at or below the window guarantees the oldest retained capture cannot reach back far
        // enough to fill it, so the New/Airing buckets silently under-report. Warn rather than silently
        // clamp — the fix belongs in configuration, not in a hidden override. Reported once for the
        // process rather than on every daily refresh, since the setting cannot change while running.
        if (maxAgeDays > 0 && maxAgeDays <= windowDays && !_retentionWarned)
        {
            _retentionWarned = true;
            logger.LogWarning(
                "Discovery archive keeps layers for {MaxAge} days but the bucket window looks back "
                + "{Window} days: the oldest capture cannot reach the window start, so New/Airing will be "
                + "missing recently-aired shows. Set Shows:Discovery:ArchiveMaxAgeDays above "
                + "NewShowsWindowDays.",
                maxAgeDays, windowDays);
        }

        await archive.ArchiveIfAgedAsync(json, today, _discovery.ArchiveLayerDays, ct).ConfigureAwait(false);
        await archive.PruneAsync(today, maxAgeDays, ct).ConfigureAwait(false);

        // Every retained row older than today is a layer; a row dated today IS this response, already
        // parsed into `fresh`. Newest-first order is what ScheduleMerge expects.
        var layers = new List<IReadOnlyList<DiscoveryEntry>>();
        foreach (var file in await archive.ListNewestFirstAsync(ct).ConfigureAwait(false))
        {
            if (file.FetchedOn >= today)
            {
                continue;
            }

            var older = await archive.ReadJsonAsync(file, ct).ConfigureAwait(false);
            if (older is not null)
            {
                layers.Add(ScheduleParser.Parse(older));
            }
        }

        return ScheduleMerge.Merge(fresh, layers);
    }

    private bool IsFresh() =>
        DateTimeOffset.UtcNow - _lastRefreshUtc < TimeSpan.FromHours(Math.Max(1, _discovery.RefreshHours));

    private async Task<string> LoadJsonAsync(CancellationToken ct)
    {
        // Local-file mode: parse the user's saved sample. Read fresh each refresh so the user can drop in
        // a newer export without a rebuild.
        if (_localFileMode)
        {
            if (!File.Exists(paths.DiscoverySchedulePath))
            {
                throw new FileNotFoundException(
                    $"Discovery schedule source not found: {paths.DiscoverySchedulePath}",
                    paths.DiscoverySchedulePath);
            }

            return await File.ReadAllTextAsync(paths.DiscoverySchedulePath, ct).ConfigureAwait(false);
        }

        // Live mode: fetch /schedule/full through the Tvmaze named client (proxy/throttle/Polly wired there),
        // routed through http_cache so the response is cached and revalidated rather than refetched whole.
        // The cache store is scoped to an EF context while this source is a singleton, so open a short scope.
        using var scope = scopeFactory.CreateScope();
        var cache = scope.ServiceProvider.GetRequiredService<Tvmaze.HttpCacheStore>();
        var client = httpClientFactory.CreateClient("Tvmaze");

        CacheFetchResult result;
        try
        {
            result = await cache.GetOrCreateAsync(client, ScheduleUrl, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // the caller cancelled; not a provider failure
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            // R14: name the host, never return empty.
            logger.LogError(ex, "TVmaze transport failure for {Url} on {Host}.", ScheduleUrl, Host);
            throw new TvmazeUnavailableException($"Could not reach {Host} (GET {ScheduleUrl}): {ex.Message}", ex);
        }

        if (result.Success && result.Body is not null)
        {
            return result.Body;
        }

        var status = result.Status.HasValue
            ? ((int)result.Status.Value).ToString(CultureInfo.InvariantCulture)
            : "unknown";
        logger.LogError("TVmaze returned {Status} for {Url} on {Host}.", status, ScheduleUrl, Host);
        throw new TvmazeUnavailableException($"{Host} returned HTTP {status} for GET {ScheduleUrl}.");
    }

    // The singleton is disposed when the container shuts down; the gate is the only disposable it owns.
    public void Dispose() => _gate.Dispose();
}
