using Microsoft.Extensions.Logging;

namespace Shows.Infrastructure.Http;

/// <summary>
/// Enforces the per-host politeness rules for one named client: serial requests and a minimum
/// interval between them (<c>ARCHITECTURE.md</c> §5).
/// </summary>
/// <remarks>
/// <para>
/// <b>Central by construction.</b> <c>MinRequestIntervalMs</c> is applied here rather than at call
/// sites because a job that loops and forgets throttling is the main risk to both external services,
/// and the failure is invisible until the service blocks the user.
/// </para>
/// <para>
/// Registered as <b>transient</b>, and the semaphore is a singleton keyed by client name: a
/// <see cref="DelegatingHandler"/> is pooled per handler chain, so a per-instance semaphore would
/// throttle each pooled copy separately and the limit would be meaningless.
/// </para>
/// </remarks>
public sealed class ThrottlingHandler(
    string clientName,
    ThrottleState state,
    int minIntervalMs,
    ILogger logger) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        await state.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Measured from the previous request's start, not its completion: an interval counted
            // from completion would let a fast provider be hammered at (interval + latency).
            var now = DateTimeOffset.UtcNow;
            var earliest = state.LastRequestAt.AddMilliseconds(minIntervalMs);
            if (state.LastRequestAt != default && now < earliest)
            {
                var delay = earliest - now;
                logger.LogDebug(
                    "Throttling {Client} request to {Host} for {Delay}",
                    clientName,
                    request.RequestUri?.Host,
                    delay);
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }

            state.LastRequestAt = DateTimeOffset.UtcNow;

            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            state.Gate.Release();
        }
    }
}

/// <summary>Shared per-client throttle state. One instance per named client, registered as singleton.</summary>
public sealed class ThrottleState
{
    /// <summary>Serialises all requests to one host, matching TVmaze's politeness expectations.</summary>
    public SemaphoreSlim Gate { get; } = new(1, 1);

    public DateTimeOffset LastRequestAt { get; set; }

    /// <summary>Requests sent since process start, for the per-run cap and for the step log.</summary>
    public int RequestCount { get; set; }
}
