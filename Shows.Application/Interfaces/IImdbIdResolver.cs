namespace Shows.Application;

/// <summary>
/// Resolves a myshows id to an IMDb id by scraping myshows.me (SPEC F1.2).
/// </summary>
/// <remarks>
/// <para>
/// Reachable only from <c>Shows.Jobs.ScrapeMyShows</c> (<c>ARCHITECTURE.md</c> §5, scraping isolation):
/// it is the most fragile and most ToS-sensitive integration, so it runs once, is resumable, has a hard
/// request cap, and can be deleted without touching anything else.
/// </para>
/// <para>
/// R14: a transport failure is not "this page has no IMDb id". The result must distinguish them,
/// because collapsing the two sends the user to debug the wrong thing.
/// </para>
/// </remarks>
public interface IImdbIdResolver
{
    Task<ImdbIdResolution> ResolveAsync(int myshowsId, CancellationToken cancellationToken);
}

/// <summary>
/// The outcome of one scrape, carrying enough to record <c>http_status</c> and <c>error</c> in
/// <c>staging_imdb_id</c> and to resume.
/// </summary>
/// <param name="ImdbId">The resolved <c>tt</c> id, or null when the page carried none.</param>
/// <param name="KinopoiskId">Captured opportunistically when present.</param>
/// <param name="HttpStatus">The HTTP status observed, or null when no response was received at all —
/// which is how a transport failure is told apart from a 200 with no match.</param>
/// <param name="Error">A short transport/parse error, never a response body (<c>ARCHITECTURE.md</c> §7).</param>
public sealed record ImdbIdResolution(
    string? ImdbId,
    string? KinopoiskId,
    int? HttpStatus,
    string? Error)
{
    public bool Succeeded => ImdbId is not null;

    /// <summary>True when the failure was transport, not markup — the distinction R14 requires.</summary>
    public bool IsTransportFailure => HttpStatus is null && Error is not null;
}
