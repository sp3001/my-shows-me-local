using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Shows.Application.UseCases;
using Shows.Domain;

namespace Shows.App.Avalonia.ViewModels;

/// <summary>
/// A tracked show rendered in a category tab (SPEC F4). Unlike the catalog card (4.1/4.2), this wraps a
/// real <see cref="TrackedShow"/> aggregate with the user's progress. The title is a tappable link to the
/// details page (<see cref="OpenDetailsCommand"/>, issue 8); below the progress line the show's seasons
/// render <b>directly</b> — no card-level "Episodes" wrapper — each as a collapsible row with a chevron.
/// </summary>
/// <remarks>
/// <para>
/// <b>Season collapse/expand lives here, on the list card, and is persisted (R9).</b> Each season reuses
/// <see cref="SeasonGroupViewModel"/>, whose <c>IsExpanded</c>/<c>ToggleCommand</c> write through
/// <see cref="SetSeasonExpandedUseCase"/>. The details page stays always-expanded and shows every episode
/// (improvement 11) — that is a different view and is unchanged. Untrack (ADR-024) and category changes
/// (SPEC R6) also live on the details page, not here (issue 8).
/// </para>
/// <para>
/// <b>Lazy loading (issue 7).</b> Nothing is fetched when the list renders. The card loads its seasons the
/// first time it is bound (<see cref="EnsureLoadedAsync"/>, driven by the view, not a user toggle). A
/// season that is persisted-expanded has its (filtered) rows materialized at that load; a collapsed season
/// materializes its rows only when the user first expands it (<see cref="SeasonGroupViewModel.CreateLazy"/>).
/// The read is the light <see cref="GetCardSeasonsUseCase"/>, not the full details read, so a card with
/// every season collapsed loads zero episode rows.
/// </para>
/// <para>
/// <b>The inline list is filtered (improvement 4):</b> only unwatched, already-aired regulars appear
/// (<see cref="InlineEpisodeRules"/>). The "N/M episodes" totals line is unchanged — it reflects ALL
/// regulars from the cached mapped columns, not the filtered subset. The watch toggle reuses
/// <see cref="ToggleWatchedEpisodeUseCase"/> (which owns the R5/R6 auto-finish recompute); the card never
/// recomputes progress, it reflects the <see cref="ShowProgress"/> the use case returns.
/// </para>
/// <para>
/// <b>Per-operation scope only.</b> The card lives in a Shell-hosted list, so it holds only
/// <see cref="IServiceScopeFactory"/> and opens a fresh scope per operation — never a captive scoped
/// service (same discipline as the catalog card and search VM).
/// </para>
/// </remarks>
public sealed partial class TrackedShowCardViewModel : ObservableObject
{
    private readonly IServiceScopeFactory _scopeFactory;

    // The totals shown on the card. Seeded from the read model's cached mapped columns and refreshed from
    // the ShowProgress the watch toggle returns, so the "52/143 episodes" line stays correct after a toggle
    // without the card recomputing anything (SPEC R5/R6 live in the use case).
    private int _watchedRegular;
    private int _totalRegular;

    // Guards the one-time season load so re-binding never refetches.
    private bool _seasonsLoaded;

    // Only the Started list is an episode watch tracker, so only its cards load and render the inline
    // season/episode list. Later/Cancelled/Finished cards skip the per-card season query entirely (the
    // dominant cost when a category holds hundreds of shows) and render just the poster/title/progress.
    private readonly bool _showSeasons;

    public TrackedShowCardViewModel(TrackedShow show, IServiceScopeFactory scopeFactory, bool showSeasons)
    {
        Show = show;
        _scopeFactory = scopeFactory;
        _showSeasons = showSeasons;
        _watchedRegular = show.WatchedRegular;
        _totalRegular = show.TotalRegular;

        var title = TitleRules.Select(show.Title, show.TitleLocalized);
        PrimaryTitle = title.Primary;
        SecondaryTitle = title.Secondary;

        // Seed the pin flag (improvement 18) from the read model. A live toggle updates the observable below;
        // the domain object here is not mutated, so the next fresh read (which carries the persisted flag)
        // drives the signature and rebuilds the card if it changed.
        _isPinned = show.IsPinned;

        // Seed the signature from the show alone; a Started card overwrites it in ApplyBatchedSeasons once
        // its seasons are known. The list compares this against a freshly-read signature to decide whether a
        // cached card can be reused (UI memoization — see TrackingViewModel).
        DataSignature = ComputeSignature(show, card: null);
    }

    /// <summary>
    /// A structural fingerprint of everything this card renders — the show's display fields plus, for a
    /// Started card, its seasons/expansion/rows/counts. The list recomputes it from a fresh read and reuses
    /// this VM only when the two match, so an unchanged card is never rebuilt (UI memoization). It is a UI
    /// cache key, not a DB fact: it is derived from data already loaded for the list, never queried.
    /// </summary>
    public string DataSignature { get; private set; }

    /// <summary>
    /// Computes the card's data fingerprint from a freshly-read show + (for Started) its batched
    /// <see cref="CardSeasons"/>. Deterministic and order-normalized so two reads of unchanged data produce
    /// the same string. <paramref name="card"/> is null for the non-tracker categories (no seasons rendered).
    /// </summary>
    public static string ComputeSignature(TrackedShow show, CardSeasons? card)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append(show.Id).Append('|')
          .Append(show.Title).Append('|')
          .Append(show.TitleLocalized).Append('|')
          .Append((int)show.Status).Append('|')
          .Append(show.PosterMediumUrl).Append('|')
          .Append(show.PosterUrl).Append('|')
          .Append(show.WatchedRegular).Append('|')
          .Append(show.TotalRegular).Append('|')
          .Append(show.HasUnwatchedSpecials).Append('|')
          .Append(show.NeedsReview).Append('|')
          .Append(show.IsPinned).Append('|')
          .Append(show.PremiereDate?.ToString("yyyy-MM-dd") ?? "-").Append('|')
          .Append(show.EndDate?.ToString("yyyy-MM-dd") ?? "-").Append('|')
          .Append(show.RatingAverage?.ToString("0.0") ?? "-").Append('|')
          .Append(show.UserRating?.ToString(CultureInfo.InvariantCulture) ?? "-").Append('|')
          .Append(show.Country ?? "-").Append('|');

        // Rating and genres now render on the card's meta line, so they belong in the fingerprint. Genre
        // names are order-normalized so a read's join-table ordering can never spuriously change the key.
        foreach (var genre in show.Genres.Select(g => g.Name).OrderBy(x => x, StringComparer.Ordinal))
        {
            sb.Append('G').Append(genre).Append(';');
        }

        if (card is null)
        {
            return sb.ToString();
        }

        // Seasons in number order (the read's order), each id:number.
        foreach (var season in card.Seasons)
        {
            sb.Append('S').Append(season.Id).Append(':').Append(season.SeasonNumber).Append(';');
        }

        // Expanded ids sorted (HashSet order is not stable across reads).
        foreach (var id in card.ExpandedSeasonIds.OrderBy(x => x))
        {
            sb.Append('E').Append(id).Append(';');
        }

        // For each expanded season, its ordered row ids — in season order so the sequence is deterministic.
        foreach (var season in card.Seasons)
        {
            if (card.EpisodesByExpandedSeason.TryGetValue(season.Id, out var rows))
            {
                sb.Append('R').Append(season.Id).Append(':');
                foreach (var ep in rows)
                {
                    sb.Append(ep.Id).Append(',');
                }
                sb.Append(';');
            }
        }

        // Per-season displayable counts, sorted by season id.
        foreach (var kv in card.DisplayableCountBySeason.OrderBy(k => k.Key))
        {
            sb.Append('C').Append(kv.Key).Append('=').Append(kv.Value).Append(';');
        }

        return sb.ToString();
    }

    public TrackedShow Show { get; }

    public long TrackedShowId => Show.Id;

    public string PrimaryTitle { get; }

    public string? SecondaryTitle { get; }

    public bool HasSecondaryTitle => !string.IsNullOrEmpty(SecondaryTitle);

    public string? PosterUrl => Show.PosterMediumUrl ?? Show.PosterUrl;

    public bool HasPoster => !string.IsNullOrEmpty(PosterUrl);

    /// <summary>
    /// The show's airing state next to the title (improvement 3). Uses the mapped
    /// <see cref="ShowStatus"/> through <see cref="ShowStatusDisplay"/> — never the raw provider string
    /// (SPEC R12) — so it reads "In Development" with a space rather than the enum's PascalCase name.
    /// </summary>
    public string StatusText => ShowStatusDisplay.Text(Show.Status);

    /// <summary>
    /// The compact season/episode summary on the list card (issue 6). Seeded from the cached mapped
    /// columns <c>watched_regular</c>/<c>total_regular</c> the read model carries, then refreshed from the
    /// <see cref="ShowProgress"/> a watch toggle returns — the UI never queries episodes or source tables
    /// for it (SPEC P5) and never recomputes it. There is no cached season count, so the line reports
    /// episodes watched of total rather than a season number.
    /// </summary>
    public string EpisodesText =>
        _totalRegular > 0
            ? $"{_watchedRegular.ToString(CultureInfo.InvariantCulture)}/{_totalRegular.ToString(CultureInfo.InvariantCulture)} episodes"
            : "no episodes yet";

    public double ProgressFraction =>
        _totalRegular > 0 ? (double)_watchedRegular / _totalRegular : 0;

    public bool HasProgress => _totalRegular > 0;

    /// <summary>SPEC R4: unwatched specials surface as a note, never counted in progress.</summary>
    public bool ShowSpecialsNote => Show.HasUnwatchedSpecials;

    /// <summary>The import flagged this show's watched-state reconstruction as a guess.</summary>
    public bool ShowNeedsReview => Show.NeedsReview;

    // ---- Pinning (improvement 18) ----

    /// <summary>
    /// Whether the user pinned this show to the top of the Started list. Seeded from the read model and
    /// updated by <see cref="TogglePinCommand"/>. The list sorts pinned cards above the active sort, so a
    /// toggle re-runs the projection (via <see cref="PinToggled"/>) and the card moves immediately.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PinText))]
    private bool _isPinned;

    /// <summary>Only the Started tracker offers pinning (improvement 18), so the toggle is hidden elsewhere.</summary>
    public bool CanPin => _showSeasons;

    /// <summary>The pin control's label — "Pinned" when set, "Pin" otherwise.</summary>
    public string PinText => IsPinned ? "Pinned" : "Pin";

    /// <summary>
    /// Raised after a pin toggle persists, so the owning list re-sorts and the card moves to (or out of) the
    /// pinned block. The list subscribes when it builds the card; the card and list share a lifetime (the
    /// list's card cache owns the card), so no unsubscribe is needed. The sender is this card.
    /// </summary>
    public event EventHandler? PinToggled;

    /// <summary>
    /// Flips the pin flag through <see cref="SetShowPinnedUseCase"/> (a user decision, stored — SPEC P4),
    /// then reflects it and asks the list to re-sort. Runs in its own scope like every card operation.
    /// </summary>
    [RelayCommand]
    private async Task TogglePinAsync()
    {
        bool pinned;
        await using (var scope = _scopeFactory.CreateAsyncScope())
        {
            var useCase = scope.ServiceProvider.GetRequiredService<SetShowPinnedUseCase>();
            pinned = await useCase.ExecuteAsync(TrackedShowId, !IsPinned, CancellationToken.None)
                .ConfigureAwait(false);
        }

        // The command completes on a background thread (ConfigureAwait(false)); the observable and the
        // list's re-sort (which mutates bound collections) must run on the main thread.
        MainThread.BeginInvokeOnMainThread(() =>
        {
            IsPinned = pinned;
            PinToggled?.Invoke(this, EventArgs.Empty);
        });
    }

    public string YearText =>
        Show.PremiereDate?.Year.ToString(CultureInfo.InvariantCulture) ?? string.Empty;

    /// <summary>TVmaze's average rating, formatted for display; null when the show has none.</summary>
    public string? RatingText =>
        Show.RatingAverage?.ToString("0.0", CultureInfo.InvariantCulture);

    /// <summary>
    /// The user's own 1-10 rating rendered as a highlighted chip on the card's first line (next to the
    /// status chip); null when the show is unrated, so the chip hides. Distinct from <see cref="RatingText"/>
    /// (TVmaze's catalog rating, shown on the meta line).
    /// </summary>
    public string? UserRatingText =>
        Show.UserRating is { } rating
            ? rating.ToString(CultureInfo.InvariantCulture)
            : null;

    public bool HasUserRating => Show.UserRating is not null;

    /// <summary>
    /// The card's meta line — year · genre · genre · country · ★rating — mirroring the discovery card's
    /// dot-separated meta line (the tracked lists previously showed none of rating/genres/year). Assembled
    /// here rather than in XAML so an absent part leaves no dangling separator (a Span has no IsVisible).
    /// Status is deliberately not repeated here — it already renders as a chip beside the title.
    /// </summary>
    public string MetaText
    {
        get
        {
            var parts = new List<string>();
            if (YearText is { Length: > 0 } year)
            {
                parts.Add(year);
            }

            foreach (var genre in Show.Genres)
            {
                if (!string.IsNullOrWhiteSpace(genre.Name))
                {
                    parts.Add(genre.Name);
                }
            }

            // The country after the genres (improvement 21). Read straight from the mapped column, which
            // already carries the language fallback — the same label the details page and the Discovery
            // facet use, so a show never reads one country on one page and nothing on another.
            if (!string.IsNullOrWhiteSpace(Show.Country))
            {
                parts.Add(Show.Country!);
            }

            if (RatingText is { Length: > 0 } rating)
            {
                parts.Add("★" + rating);
            }

            return string.Join("  ·  ", parts);
        }
    }

    public bool HasMetaText => MetaText.Length > 0;

    /// <summary>Navigate to the show's details page (Phase 6). Shell navigation is a UI concern, so the
    /// card drives it directly rather than routing through a use case.</summary>
    [RelayCommand]
    private Task OpenDetailsAsync() =>
        Shell.Current.GoToAsync($"ShowDetails?id={TrackedShowId.ToString(CultureInfo.InvariantCulture)}");

    // ---- Seasons, rendered directly (no card-level "Episodes" toggle) ----

    /// <summary>
    /// The show's seasons, newest-first, each a collapsible row. Expanded seasons carry their filtered
    /// rows immediately; collapsed seasons load theirs on first expand. Regulars only — the specials
    /// season (number 0) is omitted here and lives on the details page (improvement 6 / SPEC R4).
    /// </summary>
    public ObservableCollection<SeasonGroupViewModel> Seasons { get; } = [];

    public bool HasSeasons => Seasons.Count > 0;

    /// <summary>True while the first season load is in flight, so the card can show a busy indicator.</summary>
    [ObservableProperty]
    private bool _isBusy;

    /// <summary>
    /// Loads the card's seasons the first time it is bound (the view calls this from
    /// <c>OnBindingContextChanged</c> — it is not a user toggle). Idempotent: a re-bind never refetches.
    /// </summary>
    public Task EnsureLoadedAsync()
    {
        if (_seasonsLoaded || !_showSeasons)
        {
            // Non-tracker categories never load seasons (improvement: faster Later/Cancelled/Finished).
            return Task.CompletedTask;
        }

        _seasonsLoaded = true;
        return LoadSeasonsAsync();
    }

    /// <summary>
    /// Builds the card's seasons synchronously from data the list loaded in one batch
    /// (<see cref="GetStartedCardsUseCase"/>), instead of the card issuing its own query via
    /// <see cref="EnsureLoadedAsync"/>. The Started list calls this for every card during its single batched
    /// load, so the whole list costs five set-based queries and zero per-card round-trips (Started list
    /// performance). Must be called on the main thread (it mutates the bound collection). A card that has
    /// already loaded — or that does not show seasons — ignores the call.
    /// </summary>
    public void ApplyBatchedSeasons(CardSeasons card)
    {
        if (_seasonsLoaded || !_showSeasons)
        {
            return;
        }

        _seasonsLoaded = true;
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        Seasons.Clear();
        foreach (var group in BuildSeasons(card, today))
        {
            Seasons.Add(group);
        }

        // Record what this card now renders so the list can recognize an unchanged card on the next load
        // and reuse it without rebuilding (UI memoization — see TrackingViewModel).
        DataSignature = ComputeSignature(Show, card);
        OnPropertyChanged(nameof(HasSeasons));
    }

    private async Task LoadSeasonsAsync()
    {
        IsBusy = true;
        try
        {
            var today = DateOnly.FromDateTime(DateTime.UtcNow);
            CardSeasons card;
            await using (var scope = _scopeFactory.CreateAsyncScope())
            {
                var read = scope.ServiceProvider.GetRequiredService<GetCardSeasonsUseCase>();
                card = await read.ExecuteAsync(TrackedShowId, today, CancellationToken.None)
                    .ConfigureAwait(false);
            }

            var groups = BuildSeasons(card, today);
            // The continuation is off the UI thread (ConfigureAwait(false)); the bound collection and the
            // IsBusy flag must be touched on the main thread.
            MainThread.BeginInvokeOnMainThread(() =>
            {
                Seasons.Clear();
                foreach (var group in groups)
                {
                    Seasons.Add(group);
                }

                OnPropertyChanged(nameof(HasSeasons));
                IsBusy = false;
            });
        }
        catch
        {
            MainThread.BeginInvokeOnMainThread(() => IsBusy = false);
        }
    }

    // Build the season groups newest-first (SeasonEpisodeOrdering.SeasonsDescending), dropping the specials
    // season. A season with no displayable episodes is dropped EAGERLY from the card's count map (issue 11)
    // — before it is ever shown, not only after a failed expand. An expanded season is built eagerly from
    // the rows the read model already filtered; a collapsed season is created lazy so its rows are fetched
    // only on first expand, but it still carries its eager count for the header.
    private List<SeasonGroupViewModel> BuildSeasons(CardSeasons card, DateOnly today)
    {
        var groups = new List<SeasonGroupViewModel>();
        foreach (var season in card.Seasons
                     .Where(s => s.SeasonNumber != 0)
                     .OrderBy(s => s, SeasonEpisodeOrdering.SeasonsDescending))
        {
            // issue 11: a season with nothing to show (all watched or all unaired, after the improvement-4
            // filter) never appears — the eager count decides this for collapsed AND expanded seasons alike,
            // so an empty header is never rendered even for a season the user has not opened.
            var count = card.DisplayableCountBySeason.TryGetValue(season.Id, out var c) ? c : 0;
            if (count == 0)
            {
                continue;
            }

            if (card.ExpandedSeasonIds.Contains(season.Id))
            {
                var rows = card.EpisodesByExpandedSeason.TryGetValue(season.Id, out var eps)
                    ? eps
                    : [];
                groups.Add(new SeasonGroupViewModel(
                    season,
                    rows,
                    EmptyWatched,
                    isExpanded: true,
                    _scopeFactory,
                    TrackedShowId,
                    OnEpisodeToggled,
                    removeWatchedOnToggle: true,
                    onGroupEmptied: OnSeasonEmptied));
            }
            else
            {
                // A collapsed season carries its eager count in the header and is created lazy so its rows
                // load only on first expand (issue 7). The count already proved it is non-empty, so it is
                // shown now rather than waiting for an expand that might find it empty.
                groups.Add(SeasonGroupViewModel.CreateLazy(
                    season,
                    () => LoadSeasonCoreAsync(season.Id, today),
                    count,
                    EmptyWatched,
                    _scopeFactory,
                    TrackedShowId,
                    OnEpisodeToggled,
                    onGroupEmptied: OnSeasonEmptied));
            }
        }

        return groups;
    }

    // issue 11: a season that has no displayable episodes is removed from the card. Runs on the main thread
    // (its callers — the lazy-load completion and the last-row-watched callback — marshal there). The card
    // renders inside a plain VerticalStackLayout (not a CollectionView), so removing the group re-measures
    // the card automatically — no manual invalidation is needed (issue 10).
    private void OnSeasonEmptied(SeasonGroupViewModel group)
    {
        if (Seasons.Remove(group))
        {
            OnPropertyChanged(nameof(HasSeasons));
        }
    }

    // The lazy per-season fetch, handed to a collapsed SeasonGroupViewModel. Runs in its own scope.
    private async Task<IReadOnlyList<Episode>> LoadSeasonCoreAsync(long seasonId, DateOnly today)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var read = scope.ServiceProvider.GetRequiredService<GetCardSeasonsUseCase>();
        return await read.ExecuteSeasonAsync(TrackedShowId, seasonId, today, CancellationToken.None)
            .ConfigureAwait(false);
    }

    // The card's inline rows arrive pre-filtered (unwatched + already-aired) from the read model, so the
    // watched set is not consulted when building rows — an empty set keeps every given row.
    private static readonly IReadOnlySet<long> EmptyWatched = new HashSet<long>();

    // The watch toggle's callback (fired by EpisodeRowViewModel after ToggleWatchedEpisodeUseCase runs).
    // Reflect the returned progress on the card's totals line — the card never recomputes (SPEC R5/R6).
    private void OnEpisodeToggled(ShowProgress progress)
    {
        _watchedRegular = progress.WatchedRegular;
        _totalRegular = progress.TotalRegular;
        OnPropertyChanged(nameof(EpisodesText));
        OnPropertyChanged(nameof(ProgressFraction));
        OnPropertyChanged(nameof(HasProgress));
    }
}
