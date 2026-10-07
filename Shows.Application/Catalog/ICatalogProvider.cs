namespace Shows.Application;

/// <summary>
/// The second-provider seam (SPEC F2). One implementation today (TVmaze); adding another must not
/// touch the UI.
/// </summary>
/// <remarks>
/// <para>
/// Every method is a <b>read</b>. Nothing here writes anything except the transport cache
/// (<c>ARCHITECTURE.md</c> §5, SPEC R8a).
/// </para>
/// <para>
/// <b>R14 is this interface's most important constraint.</b> An unreachable host must surface as a
/// failed task naming the host, never as an empty list. A provider that returns
/// <c>Array.Empty</c> when it could not connect turns "we never reached TVmaze" into "TVmaze had
/// nothing", which reports success and leaves the database quietly incomplete — the worst outcome
/// available here, because every row count still looks plausible.
/// </para>
/// </remarks>
public interface ICatalogProvider
{
    Task<IReadOnlyList<CatalogShow>> SearchAsync(string query, CancellationToken cancellationToken);

    Task<CatalogShow?> GetShowAsync(int tvmazeId, CancellationToken cancellationToken);

    Task<IReadOnlyList<EpisodeCatalogInfo>> GetEpisodesAsync(
        int tvmazeId, CancellationToken cancellationToken);

    /// <summary>The import's <c>/lookup/shows?imdb=</c> step (SPEC F1.3).</summary>
    Task<int?> LookupByImdbIdAsync(string imdbId, CancellationToken cancellationToken);

    // NOTE: the Discovery feeds (issue 19) are NOT here. They used to be GetNewShowsAsync/GetUpcomingAsync
    // over /schedule/new and /schedule/upcoming; those are replaced by IDiscoverySource, which queries a
    // cached /schedule/full snapshot rather than hitting a live endpoint per request.

    /// <summary>
    /// A show's alternative titles from <c>/shows/{id}/akas</c> (step 7.5), as the provider returned them —
    /// <b>raw</b> <c>(Name, CountryCode, CountryName)</c>, in the API's array order. Choosing the target
    /// country is a policy decision that belongs above the provider (the provider must not hard-code a
    /// country; the user's country is configuration), so this returns the alternates, not a resolved title.
    /// </summary>
    /// <remarks>
    /// R14: an unreachable host throws naming the host; a genuine empty list is a real "TVmaze has no akas".
    /// </remarks>
    Task<IReadOnlyList<ShowAka>> GetAkasAsync(int tvmazeId, CancellationToken cancellationToken);

    /// <summary>
    /// The per-show <c>updated</c> timestamp (unix seconds) for <b>every</b> show TVmaze knows about, from
    /// the single <c>/updates/shows</c> endpoint — keyed by TVmaze id. One call returns the whole map, so a
    /// caller that wants to know which of its tracked shows changed fetches this once and compares against
    /// its own stored <c>provider_updated</c>, rather than re-fetching every show. The response is large
    /// (every show on the site), which is exactly why it is fetched once and cached, not per show.
    /// </summary>
    /// <remarks>
    /// R14: an unreachable host throws naming the host; the map is never empty on a transport failure.
    /// A show id absent from the returned map carries no update signal — the caller treats that as
    /// "no newer version known", not "refresh anyway".
    /// </remarks>
    Task<IReadOnlyDictionary<int, long>> GetShowUpdateTimestampsAsync(CancellationToken cancellationToken);
}

/// <summary>
/// One alternative title as TVmaze returned it (step 7.5). The endpoint keys on <b>country</b>, not
/// language — there is no <c>language</c> field (probed live 2026-09-30) — so the pick is by
/// <see cref="CountryCode"/>. Raw strings, not a resolved title: the country choice is policy, above the
/// provider.
/// </summary>
/// <param name="Name">The aka title, verbatim.</param>
/// <param name="CountryCode"><c>country.code</c> — 'RU', 'CN', ... The pick key.</param>
/// <param name="CountryName"><c>country.name</c>, verbatim.</param>
public sealed record ShowAka(string Name, string CountryCode, string? CountryName);

/// <summary>
/// An episode as the catalog knows it, before it becomes a row.
/// </summary>
/// <remarks>
/// Carries the raw provider strings rather than enums, because translating them is the mapper's job
/// and happens at exactly one point of contact (SPEC R12). A provider that pre-translated them here
/// would put the vocabulary dictionary in two places.
/// </remarks>
public sealed record EpisodeCatalogInfo(
    int TvmazeEpisodeId,
    int SeasonNumber,
    int? EpisodeNumber,
    string? Title,
    string? TypeRaw,
    string? AirDateRaw,
    string? AirStampRaw,
    int? RuntimeMinutes,
    string? ImageUrl,
    string? ImageMediumUrl,
    string? SummaryHtml,
    string? ImdbId);
