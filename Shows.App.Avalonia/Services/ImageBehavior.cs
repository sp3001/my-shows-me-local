using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using Shows.App.Avalonia.Services;

namespace Shows.App.Avalonia;

/// <summary>
/// Loads a remote poster into an <see cref="Image"/> from a URL string. MAUI's <c>Image.Source</c>
/// accepts a URI string directly; Avalonia's accepts only an <see cref="IBitmap"/>, so this attached
/// property bridges the gap: set <c>local:ImageBehavior.Url="{Binding PosterUrl}"</c> and the bitmap is
/// fetched asynchronously and assigned to <see cref="Image.Source"/> once it arrives.
/// </summary>
/// <remarks>
/// <para>
/// A shared <see cref="HttpClient"/> and an in-memory URL→bitmap cache back it, so a virtualized list
/// that recycles a card does not re-download the same poster. A failed fetch leaves the image blank (the
/// card's placeholder shows only when the URL itself is empty, matching the MAUI card's behaviour).
/// </para>
/// <para>
/// This lives in the View layer and uses only <c>System.Net</c>/<c>System.Net.Http</c> — never
/// Infrastructure — so the layering rule (no View names an Infrastructure type) holds. Like the TVmaze
/// API client (SPEC R14), the image client can be routed through a proxy: the composition root turns the
/// <c>Proxy:Images</c> config value into an <see cref="IWebProxy"/> (a BCL type) and hands it to
/// <see cref="Configure"/>, so poster loading reaches a host that is otherwise unreachable directly.
/// When no proxy is configured the client goes direct, exactly as the MAUI head's image loading does.
/// </para>
/// </remarks>
public sealed class ImageBehavior
{
    private ImageBehavior()
    {
    }

    // Built once in Configure with the configured proxy (SPEC R14). The lazy fallback keeps the type usable
    // if a fetch somehow runs before the composition root configures it — direct, no proxy, same as before.
    private static HttpClient? _http;

    private static HttpClient Http => _http ??= BuildClient(null);

    private static HttpClient BuildClient(IWebProxy? proxy)
    {
        // CA2000: the handler is owned by the HttpClient returned here — it is disposed when the client is
        // disposed (the client lives for the process lifetime). The analyzer cannot see that ownership
        // transfer, same as the cached-bitmap case below.
#pragma warning disable CA2000
        return new HttpClient(new SocketsHttpHandler
        {
            // Explicit direct when no proxy is configured — UseProxy=false, never "inherit the machine's
            // ambient settings" (the same R14 rule the named clients follow in ProxyConfig).
            UseProxy = proxy is not null,
            Proxy = proxy,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
        })
        { Timeout = TimeSpan.FromSeconds(20) };
#pragma warning restore CA2000
    }

    private static ILogger Log => _log ??= AppLog.For<ImageBehavior>();

    private static ILogger? _log;

    // Tier 1: URL → decoded bitmap for this session. Holds only successes; a failure is never stored here
    // so a later appearance retries rather than serving a cached blank.
    private static readonly ConcurrentDictionary<string, Bitmap> Cache = new();

    // A failed URL and when it last failed. Within the cooldown a re-appearance does not re-hit the
    // network (avoids hammering a persistently-failing host); after it, the next appearance retries.
    private static readonly ConcurrentDictionary<string, DateTimeOffset> Failed = new();
    private static readonly TimeSpan FailureCooldown = TimeSpan.FromSeconds(60);

    // Tier 2: the disk cache directory (cache/posters), supplied by the composition root. Null until
    // Configure runs, in which case the behaviour degrades to memory + network only.
    private static string? _cacheDirectory;

    public static readonly AttachedProperty<string?> UrlProperty =
        AvaloniaProperty.RegisterAttached<ImageBehavior, Image, string?>("Url");

    public static void SetUrl(Image element, string? value) => element.SetValue(UrlProperty, value);

    public static string? GetUrl(Image element) => element.GetValue(UrlProperty);

    static ImageBehavior()
    {
        UrlProperty.Changed.AddClassHandler<Image>((image, e) => OnUrlChanged(image, e.NewValue as string));
    }

    /// <summary>
    /// Points the disk cache at a directory and configures the image client's proxy. Called once from the
    /// composition root with <c>DataPaths.PosterCacheDirectory</c> and the <see cref="IWebProxy"/> built
    /// from <c>Proxy:Images</c> (null = direct) before the UI starts. Idempotent.
    /// </summary>
    public static void Configure(string cacheDirectory, IWebProxy? proxy = null)
    {
        _cacheDirectory = cacheDirectory;
        _http = BuildClient(proxy);
    }

    private static void OnUrlChanged(Image image, string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            image.Source = null;
            return;
        }

        // Tier 1: already decoded this session.
        if (Cache.TryGetValue(url, out var cached))
        {
            image.Source = cached;
            return;
        }

        // Tier 2: on disk from a previous session — decode it, promote to memory, no network.
        // CA2000: the decoded Bitmap is a long-lived cached resource (stored in Cache and assigned to
        // Image.Source), not a disposable to release here — ownership transfers, same as the LoadAsync path.
#pragma warning disable CA2000
        if (TryLoadFromDisk(url) is { } diskBitmap)
        {
            Cache[url] = diskBitmap;
            image.Source = diskBitmap;
            return;
        }
#pragma warning restore CA2000

        // Recently failed and still inside the cooldown: leave the placeholder, do not re-hit the network.
        if (InFailureCooldown(url))
        {
            return;
        }

        // Register this image against the URL so it receives the bitmap when the fetch completes, even if
        // it was not the card that started the download (the in-flight race: a later card requesting the
        // same URL must still be filled, not left blank). Then kick off the fetch once per URL.
        Watch(url, image);
        _ = LoadAsync(url);
    }

    private static async System.Threading.Tasks.Task LoadAsync(string url)
    {
        // Avoid racing duplicate downloads for the same URL across recycled cards. Only the first card to
        // request a URL proceeds to fetch; the others registered themselves in Watch and are filled on
        // completion below.
        if (!TryBeginFetch(url))
        {
            return;
        }

        Bitmap? bitmap = null;
        try
        {
            // One retry: tvmaze resets idle keep-alive connections (10054), and a fresh request usually
            // succeeds. The retry is immediate; the cooldown below is the slower backstop.
            var bytes = await GetBytesWithRetryAsync(url).ConfigureAwait(false);
            using var ms = new MemoryStream(bytes);
            bitmap = new Bitmap(ms);

            SaveToDisk(url, bytes);
            Cache[url] = bitmap;
            Failed.TryRemove(url, out _);
        }
        catch (Exception ex)
        {
            // A failed poster leaves the image blank (the placeholder shows) — the list still works, so
            // this is a warning, not an error. The URL is NOT cached, so a later appearance retries after
            // the cooldown rather than sticking on a placeholder for the whole session.
            bitmap = null;
            Failed[url] = DateTimeOffset.UtcNow;
            Log.LogWarning(ex, "Poster fetch failed for {Url}", url);
        }
        finally
        {
            // Release the in-flight guard so a future appearance can retry a failed URL.
            InFlight.TryRemove(url, out _);
        }

        // Assign on the UI thread to every image still bound to this URL (any may have been recycled to a
        // different card while the download was in flight). This is the fix: previously only the single
        // image that started the fetch was assigned, so a card that requested an already-in-flight URL — or
        // whose originating card scrolled away — stayed blank for the whole session.
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            foreach (var target in TakeWatchers(url))
            {
                if (GetUrl(target) == url)
                {
                    target.Source = bitmap;
                }
            }
        });
    }

    // Fetch the poster bytes, retrying once on a transport failure. A connection reset on a pooled
    // keep-alive socket is transient and a second request over a fresh socket normally succeeds.
    private static async System.Threading.Tasks.Task<byte[]> GetBytesWithRetryAsync(string url)
    {
        try
        {
            return await Http.GetByteArrayAsync(new Uri(url)).ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            return await Http.GetByteArrayAsync(new Uri(url)).ConfigureAwait(false);
        }
    }

    private static bool InFailureCooldown(string url) =>
        Failed.TryGetValue(url, out var when)
        && DateTimeOffset.UtcNow - when < FailureCooldown;

    // A stable filesystem name for a URL: SHA-256 hex, so any valid URL maps to a safe filename with no
    // collisions in practice and no path-traversal risk from the URL's own characters.
    private static string CacheFilePath(string url)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url)));
        return Path.Combine(_cacheDirectory!, hash + ".img");
    }

    // Decode a cached poster from disk, or null when absent/unreadable. Ownership of the returned bitmap
    // transfers to the caller (it is stored in Cache and assigned to Image.Source), matching the
    // LoadAsync path — so it is intentionally not disposed here.
    private static Bitmap? TryLoadFromDisk(string url)
    {
        if (_cacheDirectory is null)
        {
            return null;
        }

        try
        {
            var path = CacheFilePath(url);
            if (!File.Exists(path))
            {
                return null;
            }

            using var fs = File.OpenRead(path);
            return new Bitmap(fs);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            // A missing/corrupt cache file is not fatal — fall through to a network fetch. Warning level:
            // the poster still loads from the network, only the cache shortcut is lost.
            Log.LogWarning(ex, "Poster cache file unreadable for {Url}; refetching.", url);
            return null;
        }
    }

    private static void SaveToDisk(string url, byte[] bytes)
    {
        if (_cacheDirectory is null)
        {
            return;
        }

        try
        {
            var path = CacheFilePath(url);
            // Write to a temp file then move, so a crash mid-write never leaves a truncated poster that
            // would decode as corrupt on the next launch.
            var temp = path + ".tmp";
            File.WriteAllBytes(temp, bytes);
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Failing to persist the cache only costs a re-download next session; the poster is already
            // shown this session. Warning, not error.
            Log.LogWarning(ex, "Could not write poster cache for {Url}.", url);
        }
    }

    // Guard so only one fetch starts per URL. Returns true if the caller should proceed.
    private static readonly ConcurrentDictionary<string, byte> InFlight = new();

    private static bool TryBeginFetch(string url) => InFlight.TryAdd(url, 0);

    // Every image currently asking for a URL, so a completed fetch can fill all of them. Weak references
    // so a recycled/collected card does not leak: the list is drained when the fetch completes.
    private static readonly ConcurrentDictionary<string, ConcurrentBag<WeakReference<Image>>> Watchers = new();

    private static void Watch(string url, Image image) =>
        Watchers.GetOrAdd(url, _ => new ConcurrentBag<WeakReference<Image>>()).Add(new WeakReference<Image>(image));

    // Drain and return the live images registered for a URL, dropping any collected since registration.
    private static List<Image> TakeWatchers(string url)
    {
        if (!Watchers.TryRemove(url, out var bag))
        {
            return [];
        }

        var live = new List<Image>();
        foreach (var weak in bag)
        {
            if (weak.TryGetTarget(out var target) && target is not null)
            {
                live.Add(target);
            }
        }

        return live;
    }
}
