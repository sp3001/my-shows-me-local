using System.Net;
using Polly;
using Polly.Extensions.Http;
using Polly.Wrap;

namespace Shows.Infrastructure.Http;

/// <summary>
/// The Polly policies for the <c>Tvmaze</c> named client (ARCHITECTURE.md §5): retry with exponential
/// backoff + jitter, and a circuit breaker. Registered on the Tvmaze client only — the other two hosts
/// are reachable directly and do not need the same protection.
/// </summary>
/// <remarks>
/// <para>
/// <b>The R14 constraint shapes these policies.</b> A proxy transport failure (the dev machine cannot
/// reach TVmaze directly — <c>DATA_ANALYSIS.md</c> §9) surfaces as an <see cref="HttpRequestException"/>
/// or a timeout, NOT as an HTTP response. Retrying that would multiply the connect timeout against a
/// dead proxy (IMPLEMENTATION_PLAN step 3.1), so <see cref="Retry"/> deliberately handles only
/// <b>HTTP responses</b> that are 5xx / 408 / 429 — a provider that answered with a real error may
/// recover — and never a transport exception. Transport failures therefore propagate immediately as a
/// failed task naming the host, which is exactly what SPEC R14 requires (never "no data").
/// </para>
/// <para>
/// This is why <see cref="HttpPolicyExtensions.HandleTransientHttpError"/> is NOT used: it also handles
/// <see cref="HttpRequestException"/> and timeouts, which is precisely the dead-proxy case we must not
/// retry. The predicate here is narrower on purpose.
/// </para>
/// </remarks>
public static class TvmazePolicies
{
    /// <summary>A genuine provider error worth one more try: server-side, timeout, or rate-limit backoff.</summary>
    public static bool IsRetryableResponse(HttpResponseMessage response) =>
        (int)response.StatusCode >= 500
        || response.StatusCode == HttpStatusCode.RequestTimeout
        || response.StatusCode == HttpStatusCode.TooManyRequests;

    public static IAsyncPolicy<HttpResponseMessage> Retry() =>
        Policy<HttpResponseMessage>
            .HandleResult(IsRetryableResponse)
            .WaitAndRetryAsync(
                retryCount: 3,
                sleepDurationProvider: attempt =>
                {
                    // Exponential (1s, 2s, 4s) plus up to ~250ms of jitter so concurrent callers do not
                    // retry in lockstep and re-trigger the same 429/503.
                    var baseDelay = TimeSpan.FromSeconds(Math.Pow(2, attempt - 1));
#pragma warning disable CA5394 // retry jitter is not a security-sensitive use of randomness
                    return baseDelay + TimeSpan.FromMilliseconds(Random.Shared.Next(0, 250));
#pragma warning restore CA5394
                });

    public static IAsyncPolicy<HttpResponseMessage> CircuitBreaker() =>
        Policy<HttpResponseMessage>
            .HandleResult(IsRetryableResponse)
            .CircuitBreakerAsync(5, TimeSpan.FromSeconds(30));

    /// <summary>
    /// Retry outside, breaker inside: each retry attempt passes through the breaker, so a persistently
    /// failing provider opens the circuit rather than being retried forever.
    /// </summary>
    public static AsyncPolicyWrap<HttpResponseMessage> Combined() =>
        Policy.WrapAsync(Retry(), CircuitBreaker());
}
