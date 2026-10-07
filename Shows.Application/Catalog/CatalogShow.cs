namespace Shows.Application;

/// <summary>
/// A show as returned by a catalog provider — a search hit, a discovery-feed entry, or a lookup
/// result. <b>Not</b> a <see cref="Shows.Domain.TrackedShow"/>, and deliberately so.
/// </summary>
/// <remarks>
/// SPEC P2 and R8a: a search hit is never persisted, so it is not a domain entity — it has no id, no
/// user row, and no watched state. Returning a <c>TrackedShow</c> here would produce an object whose
/// <c>Category</c> throws, and would invite someone to save it. This is a read model, and it lives in
/// Application because that is the layer that decides what the UI is allowed to see.
/// <para>
/// <see cref="ImdbId"/> is included because the import's resolution chain is
/// <c>myshows_id → imdb_id → tvmaze_id</c>, and a provider that can supply it saves a lookup.
/// </para>
/// </remarks>
/// <param name="TvmazeId">The natural key of the show upstream.</param>
/// <param name="IsAlreadyTracked">
/// Computed by the use-case against <c>user_show</c> where <c>deleted_at IS NULL</c> — never against
/// <c>tracked_show</c> (SPEC F2).
/// </param>
public sealed record CatalogShow(
    int TvmazeId,
    string Title,
    string? TitleLocalized,
    int? Year,
    string? StatusRaw,
    string? ShowType,
    string? Language,
    IReadOnlyList<string> Genres,
    string? Overview,
    string? PosterUrl,
    string? PosterMediumUrl,
    double? RatingAverage,
    int? RuntimeMinutes,
    string? ImdbId,
    bool IsAlreadyTracked,
    // The broadcaster and its country (user-issues 19 follow-up: the card shows network · country).
    // NetworkName is network.name / webChannel.name. CountryName is the ONE display rule
    // (CountryRules.Display): the broadcaster's nested country when TVmaze has one, otherwise the show's
    // Language — so a streaming show with no country object still reads a country. The rule lives in
    // Domain and is applied by the provider, so this read model carries a finished label and the card,
    // the Discovery facet and the tracked details page can never disagree about it (improvement 21).
    string? NetworkName = null,
    string? CountryName = null,
    // The full premiere date (TVmaze <c>premiered</c>, day-precision). <see cref="Year"/> already carries
    // the year for the meta line; this is the whole date the card shows after the network. Null when the
    // source has no premiere date (a not-yet-aired show).
    DateOnly? PremiereDate = null,
    // The next episode the user is waiting for — set ONLY by Discovery, which has the schedule snapshot.
    // Search returns no episode data, so it stays null there and the card omits the line.
    CatalogNextEpisode? NextEpisode = null,
    // The show's official site (TVmaze <c>officialSite</c>). Authored catalog data with no derivation rule,
    // so it is carried on the read model (SPEC P5 permits storing what cannot be derived) and rendered as a
    // link on the untracked-details page. Null when TVmaze has none.
    string? OfficialSite = null,
    // The active <c>tracked_show</c> id when this catalog hit is already tracked (SPEC F2), else null.
    // Computed by the use case from the same batched <c>user_show</c> lookup that sets
    // <see cref="IsAlreadyTracked"/>. It lets a tracked card link straight to its tracked-details page
    // (<c>ShowDetails?id=…</c>) instead of the catalog one — a card is not a domain entity and is never
    // saved (SPEC P2/R8a), so this id is read-only navigation data, not a key the card may write.
    long? TrackedShowId = null);

/// <summary>
/// The episode a Discovery card highlights — the next one the user waits for, or (when nothing is
/// scheduled ahead) the most recent one already aired. Discovery is the only provider that has episode
/// data (the cached <c>/schedule/full</c> snapshot), so this is null everywhere else.
/// <see cref="SeasonEpisode"/> is the marker (e.g. <c>S02E05</c>), <see cref="Title"/> the episode name,
/// <see cref="AirDate"/> the day it aired or airs. <see cref="IsRecent"/> distinguishes the two: false
/// means a future episode (the card reads "Next Episode"), true means the latest known past episode
/// (the card reads "Recent Episode") — shown only when the show has nothing scheduled in the future.
/// </summary>
public sealed record CatalogNextEpisode(
    string? SeasonEpisode,
    string? Title,
    DateOnly? AirDate,
    bool IsRecent = false);
