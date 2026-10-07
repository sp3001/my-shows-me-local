using System.Net;

namespace Shows.Infrastructure.Http;

/// <summary>
/// Turns a <c>Proxy:&lt;service&gt;</c> config value into a <see cref="WebProxy"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>The rules this encodes are SPEC R14 and ADR-029, and each one is a rule because the alternative
/// failure is silent.</b>
/// </para>
/// <list type="bullet">
///   <item>
///     Empty means direct, <em>explicitly</em>. Never <c>HttpClient.DefaultProxy</c> and never an
///     ambient setting: <c>DATA_ANALYSIS.md</c> §9.5 measured that ambient resolution on the
///     development machine yields <c>HttpNoProxy</c> and behaves identically to explicit direct, so
///     inheriting it would look like support while doing nothing.
///   </item>
///   <item>
///     Accepted schemes are exactly <c>http</c>, <c>https</c>, <c>socks4</c>, <c>socks4a</c>,
///     <c>socks5</c>, or a bare <c>host:port</c> treated as HTTP. Anything else throws at handler
///     construction, so a typo fails at startup rather than silently sending traffic direct.
///   </item>
///   <item>
///     SOCKS needs no extra package on .NET 8 — verified in <c>DATA_ANALYSIS.md</c> §9.4, which is why
///     ADR-029 rejects a SOCKS handler dependency. Do not add one.
///   </item>
/// </list>
/// </remarks>
public static class ProxyConfig
{
    /// <summary>
    /// Returns null for "direct", a configured proxy otherwise.
    /// </summary>
    /// <exception cref="NotSupportedException">An unsupported scheme — thrown at startup, by design.</exception>
    public static IWebProxy? CreateProxy(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var raw = value.Trim();

        // A bare host:port is not a URI, so it cannot go through Uri.TryCreate as an absolute URI.
        // Treated as HTTP per ARCHITECTURE.md §4.
        if (!raw.Contains("://", StringComparison.Ordinal))
        {
            raw = Uri.UriSchemeHttp + Uri.SchemeDelimiter + raw;
        }

        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri)
            || string.IsNullOrEmpty(uri.Host))
        {
            throw new NotSupportedException(
                $"Proxy value '{value}' is not a valid proxy URL or host:port pair.");
        }

        // .NET's own handler accepts exactly these five schemes (DATA_ANALYSIS.md §9.4). Checking
        // here gives a message naming the configured value instead of a generic handler exception.
        var scheme = uri.Scheme;
        if (!scheme.Equals("http", StringComparison.OrdinalIgnoreCase)
            && !scheme.Equals("https", StringComparison.OrdinalIgnoreCase)
            && !scheme.Equals("socks4", StringComparison.OrdinalIgnoreCase)
            && !scheme.Equals("socks4a", StringComparison.OrdinalIgnoreCase)
            && !scheme.Equals("socks5", StringComparison.OrdinalIgnoreCase))
        {
            throw new NotSupportedException(
                $"Proxy scheme '{uri.Scheme}' is not supported. Supported: http, https, socks4, " +
                $"socks4a, socks5, or a bare host:port. Configured value: '{value}'.");
        }

        // Credentials, if ever needed, arrive as user:pass@host:port in this same value. There is
        // deliberately no separate Password key: it would put the secret where a config-file reader
        // finds it while the URL, which is logged, would lose it (STATE.md open question 7, ADR-029).
        return string.IsNullOrEmpty(uri.UserInfo)
            ? new WebProxy(uri, false)
            : new WebProxy(uri, false)
            {
                Credentials = new NetworkCredential(
                    Uri.UnescapeDataString(uri.UserInfo.Split(':', 2)[0]),
                    uri.UserInfo.Contains(':', StringComparison.Ordinal)
                        ? Uri.UnescapeDataString(uri.UserInfo.Split(':', 2)[1])
                        : string.Empty),
            };
    }
}
