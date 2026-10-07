using Shows.Infrastructure.Persistence.Sources;

namespace Shows.Infrastructure.Persistence;

/// <summary>
/// The mapper's fetch seam for the <c>MapShowAsync(int)</c> path: it returns the verbatim TVmaze
/// source rows for a show so the mapper can write them and then map. The DTO overload of the mapper
/// bypasses this entirely — a caller that already fetched (a job, which owns fetching so
/// <c>http_cache</c> and throttling apply, SPEC step 2.6) hands the rows straight to the mapper.
/// </summary>
/// <remarks>
/// Implemented by the TVmaze provider in Phase 3. Until then only the DTO overload is exercised, so
/// this is registered by that step, not here.
/// </remarks>
public interface ITvmazeShowSource
{
    /// <summary>The show and its episodes as the provider returned them, or null if the show is gone.</summary>
    Task<TvmazePayload?> FetchAsync(int tvmazeId, CancellationToken cancellationToken);
}

/// <summary>A fetched show with its episode list — the verbatim input to the mapper.</summary>
public sealed record TvmazePayload(TvmazeShow Show, IReadOnlyList<TvmazeEpisode> Episodes);
