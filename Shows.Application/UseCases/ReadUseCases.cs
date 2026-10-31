using Shows.Domain;

namespace Shows.Application.UseCases;

/// <summary>
/// One category tab's content (SPEC F4).
/// </summary>
/// <param name="Category">Which of the four (SPEC R1 — a closed set, there is no fifth).</param>
/// <param name="Shows">Already filtered <c>deleted_at IS NULL</c> by the repository.</param>
/// <param name="TotalCount">
/// The count to render in the tab header. Equal to <see cref="Shows"/>.Count today; separate because
/// the list is virtualized and will be paged, and the header must not depend on how many rows happen
/// to be materialised.
/// </param>
public sealed record CategoryOverview(
    ShowCategory Category,
    IReadOnlyList<TrackedShow> Shows,
    int TotalCount);

/// <summary>
/// Reads one category tab.
/// </summary>
/// <remarks>
/// Progress comes from the cached columns on <c>tracked_show</c>, never recomputed here — that is what
/// makes the tab query a single indexed read (<c>ix_user_show_category</c>) rather than an aggregation
/// over 15,000 episode rows on every navigation.
/// </remarks>
public sealed class GetCategoryOverviewUseCase(ITrackedShowRepository trackedShows)
{
    public async Task<CategoryOverview> ExecuteAsync(
        ShowCategory category, CancellationToken cancellationToken)
    {
        var shows = await trackedShows.ListActiveByCategoryAsync(category, cancellationToken)
            .ConfigureAwait(false);

        return new CategoryOverview(category, shows, shows.Count);
    }
}

/// <summary>
/// The most recent already-aired episode date per show, for the list's "sort by last aired" (improvement 12),
/// extended to the Later list. Computed over <c>episode</c> rather than cached on <c>tracked_show</c>
/// (SPEC P5). A show with no aired episode is absent from the map and sorts last.
/// </summary>
/// <remarks>
/// The Started list gets this for free from <see cref="GetStartedCardsUseCase"/> (it already touches every
/// episode for the inline rows). The non-Started categories skip that heavy batch, so this thin read supplies
/// the sort key alone — one grouped query, no seasons or watch rows.
/// </remarks>
public sealed class GetLastAiredByShowUseCase(IEpisodeRepository episodes)
{
    public async Task<IReadOnlyDictionary<long, DateOnly>> ExecuteAsync(
        IReadOnlyList<long> trackedShowIds, DateOnly today, CancellationToken cancellationToken) =>
        await episodes.LastAiredByShowIdsAsync(trackedShowIds, today, cancellationToken)
            .ConfigureAwait(false);
}

/// <summary>
/// Everything the details page renders (SPEC F5).
/// </summary>
/// <remarks>
/// Built entirely from local rows — no live fetch is required to open the page. The external links are
/// <b>not</b> fields: there is no URL column anywhere (SPEC P5), so the view builds them from
/// <see cref="TrackedShow.TvmazeId"/> and <see cref="TrackedShow.ImdbId"/> at render time.
/// </remarks>
/// <param name="Seasons">In season-number order, specials (0) first.</param>
/// <param name="WatchedEpisodeIds">The ids the user has marked, so the grid can render toggles without
/// a second round-trip per row.</param>
/// <param name="WatchedAtByEpisodeId">When each watched episode was marked, for the details grid's
/// "watched 2026-09-30" line (tracked shows only — an untracked catalog show has no watch state). The
/// watch rows are already loaded for <see cref="WatchedEpisodeIds"/>, so this costs no extra query; the
/// two are the same data seen two ways.</param>
/// <param name="Mismatches">The show's recorded TVmaze/IMDb disagreements (step 7.3, ADR-031), shown on
/// the details page so the user sees what IMDb offered that TVmaze did not keep. Empty when the show has
/// no IMDb id or the enrichment has not run.</param>
public sealed record ShowDetails(
    TrackedShow Show,
    IReadOnlyList<Season> Seasons,
    IReadOnlyList<Episode> Episodes,
    IReadOnlySet<long> WatchedEpisodeIds,
    IReadOnlyDictionary<long, bool> ExpandedSeasons,
    IReadOnlyList<EpisodeMismatch> Mismatches,
    IReadOnlyDictionary<long, DateTimeOffset> WatchedAtByEpisodeId);

/// <summary>
/// Reads a show's details page, including persisted season expansion (SPEC R9).
/// </summary>
public sealed class GetShowDetailsUseCase(
    ITrackedShowRepository trackedShows,
    IEpisodeRepository episodes,
    IUiStateRepository uiState)
{
    public async Task<ShowDetails> ExecuteAsync(
        long trackedShowId, CancellationToken cancellationToken)
    {
        var show = await trackedShows.FindByIdWithGenresAsync(trackedShowId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException($"No tracked show with id {trackedShowId}.");

        var showEpisodes = await episodes.ListByShowIdAsync(trackedShowId, cancellationToken)
            .ConfigureAwait(false);
        var watches = await episodes.ListWatchesByShowIdAsync(trackedShowId, cancellationToken)
            .ConfigureAwait(false);
        var states = await uiState.ListByShowIdAsync(trackedShowId, cancellationToken)
            .ConfigureAwait(false);
        var mismatches = await episodes.ListMismatchesByShowIdAsync(trackedShowId, cancellationToken)
            .ConfigureAwait(false);

        return new ShowDetails(
            show,
            show.Seasons.OrderBy(s => s.SeasonNumber).ToList(),
            showEpisodes,
            watches.Select(w => w.EpisodeId).ToHashSet(),
            states.ToDictionary(s => s.SeasonId, s => s.IsExpanded),
            mismatches,
            watches.ToDictionary(w => w.EpisodeId, w => w.WatchedAt));
    }
}

/// <summary>
/// Moves a show between categories (SPEC R6).
/// </summary>
/// <remarks>
/// Records <c>category_changed_at</c>, which is what makes R6 enforceable: a later recompute compares
/// that timestamp against the last watch change and leaves the user's choice alone if they moved the
/// show more recently than they watched anything. Without this column, auto-finish would silently
/// undo a deliberate "not finished yet" every time an episode was toggled.
/// </remarks>
public sealed class ChangeCategoryUseCase(
    ITrackedShowRepository trackedShows,
    IUserShowRepository userShows,
    IUnitOfWork unitOfWork)
{
    public async Task ExecuteAsync(
        long trackedShowId, ShowCategory category, CancellationToken cancellationToken)
    {
        // Finished is never a manual target (SPEC R5/R1): it is assigned automatically by the recompute
        // when a show is fully watched and Ended/Canceled. Offering it here would let a user pre-mark a
        // show finished and short-circuit the auto-finish rule. The UI never offers it either; this guard
        // is the defense-in-depth that keeps the invariant true even if a caller bypasses the UI. The
        // automatic path (RecomputeProgressUseCase) writes Finished through the repository directly, so
        // it is unaffected by this guard.
        if (category == ShowCategory.Finished)
        {
            throw new ArgumentException(
                "Finished is assigned automatically (SPEC R5) and cannot be set manually.",
                nameof(category));
        }

        // FindByIdAsync filters deleted_at IS NULL (SPEC R8): a soft-deleted show is not actively tracked,
        // so a category change against it is refused rather than silently mutating an untracked row.
        var show = await trackedShows.FindByIdAsync(trackedShowId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException($"No tracked show with id {trackedShowId}.");

        await userShows
            .ChangeCategoryAsync(show.Id, category, DateTimeOffset.UtcNow, cancellationToken)
            .ConfigureAwait(false);
        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>
/// Persists a season's expanded/collapsed state (SPEC R9). Owned by the UI but stored, not held in
/// memory, so a fresh launch restores the user's expansions.
/// </summary>
/// <remarks>
/// A tiny write use case rather than letting the ViewModel reach <see cref="IUiStateRepository"/> directly:
/// ViewModels talk only to use cases (<c>ARCHITECTURE.md</c> §3), and this keeps the one write to
/// <c>ui_season_state</c> behind the same seam as every other mutation.
/// </remarks>
public sealed class SetSeasonExpandedUseCase(
    IUiStateRepository uiState,
    IIdGenerator idGenerator,
    IUnitOfWork unitOfWork)
{
    public async Task ExecuteAsync(
        long trackedShowId, long seasonId, bool isExpanded, CancellationToken cancellationToken)
    {
        // Mint a fresh id for the insert path (ADR-027). It is ignored when the row already exists, so a
        // toggle that only flips an existing row costs nothing extra; a new season's row gets a real key
        // instead of the default 0 (which collided on every second season — the expand-not-saved bug).
        await uiState.SetExpandedAsync(
            trackedShowId, seasonId, isExpanded, idGenerator.NextId(), cancellationToken)
            .ConfigureAwait(false);
        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>
/// What the details-page "Fetch localized titles" button shows (step 7.5): the alternates TVmaze returned
/// and the title now chosen for the target country.
/// </summary>
/// <param name="Akas">Every aka, in the provider's order, so the button can list them all.</param>
/// <param name="PickedTitle">The title now in <c>title_localized</c>; null when no aka matched the target country.</param>
public sealed record LocalizedTitles(
    IReadOnlyList<ShowAka> Akas,
    string? PickedTitle);

/// <summary>
/// The details-page button's on-demand path (step 7.5): fetch one show's <c>/akas</c>, write the source
/// rows and map the target-country pick through the mapper (SPEC R11 — the button never writes a mapped
/// row itself), and return the alternates so the UI can list them. A single-show fetch, so it takes no
/// lease (interactive, one row).
/// </summary>
/// <remarks>
/// The target country is supplied by the composition root (Infrastructure reads it from configuration);
/// Application must not read <c>ShowsOptions</c>. R14: an unreachable TVmaze throws from the provider and
/// surfaces to the user — it is never swallowed into an empty list.
/// </remarks>
public sealed class FetchLocalizedTitlesUseCase(
    ITrackedShowRepository trackedShows,
    ICatalogProvider provider,
    IShowMapper mapper,
    IUnitOfWork unitOfWork,
    string targetCountryCode)
{
    public async Task<LocalizedTitles> ExecuteAsync(
        long trackedShowId, CancellationToken cancellationToken)
    {
        var show = await trackedShows.FindByIdAsync(trackedShowId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException($"No tracked show with id {trackedShowId}.");

        var akas = await provider.GetAkasAsync(show.TvmazeId, cancellationToken).ConfigureAwait(false);
        var result = await mapper.MapAkasAsync(show.TvmazeId, akas, targetCountryCode, cancellationToken)
            .ConfigureAwait(false);
        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return new LocalizedTitles(akas, result.PickedTitle);
    }
}
