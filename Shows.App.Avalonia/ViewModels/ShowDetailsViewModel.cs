using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Shows.App.Avalonia.Services;
using Shows.Application;
using Shows.Application.UseCases;
using Shows.Domain;

namespace Shows.App.Avalonia.ViewModels;

/// <summary>
/// The show details page (SPEC F5): header, external links, and the season/episode grid with watched
/// toggles. Rendered entirely from local rows — opening it never fetches (F5). The only live fetch is the
/// explicit "Fetch localized titles" button, which is step 7.5's on-demand path.
/// </summary>
/// <remarks>
/// <para>
/// <b>Expansion is persisted (R9).</b> Toggling a season's header writes through
/// <see cref="SetSeasonExpandedUseCase"/> so a close-and-reopen restores it. Expanding a season reveals all
/// its episodes (ADR-016) — the rows are already loaded here, so expansion is a visibility toggle, not a fetch.
/// </para>
/// <para>
/// <b>The toggle is the auto-finish trigger (R5/R6).</b> It runs <see cref="ToggleWatchedEpisodeUseCase"/>,
/// which recomputes progress and, on the last unwatched regular of an Ended/Canceled show, moves the show to
/// Finished — unless the user moved it out of Finished more recently than they watched (R6). The VM reflects
/// the returned progress and the (possibly changed) category; it never recomputes anything itself.
/// </para>
/// </remarks>
public sealed partial class ShowDetailsViewModel : ObservableObject
{
    private readonly IServiceScopeFactory _scopeFactory;

    public ShowDetailsViewModel(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory;
    }

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string? _statusMessage;

    /// <summary>The alternates the "Fetch localized titles" button returned (step 7.5), one per line.</summary>
    [ObservableProperty]
    private string? _akasText;

    // ---- Episode data differences (step 7.3 / ADR-031, grouped per improvement 9) ----
    // The disagreements are split by kind into two groups so a show with hundreds of rows (STATE: runtime
    // ~14.8k, title ~3k) stays readable. The episode-set differences (imdb_only / tvmaze_only — a show has
    // episodes one source lacks) are MAJOR and always shown; the field-level differences (title / runtime /
    // air_date_gap) are MINOR and collapsed under a View All toggle. The UI reads only the Application read
    // model's Mismatches (layering: it never touches episode_mismatch / imdb_* itself).

    /// <summary>The major disagreements (<c>imdb_only</c> / <c>tvmaze_only</c>), one per line, always shown.</summary>
    [ObservableProperty]
    private string? _majorMismatchText;

    /// <summary>The minor disagreements (<c>title</c> / <c>runtime</c> / <c>air_date_gap</c>), shown only when expanded.</summary>
    [ObservableProperty]
    private string? _minorMismatchText;

    /// <summary>Collapsed one-line count of the minor differences, e.g. "12 title / 40 runtime differences".</summary>
    [ObservableProperty]
    private string? _minorMismatchSummary;

    /// <summary>Improvement 9: whether the minor differences are expanded. A reversible toggle.</summary>
    [ObservableProperty]
    private bool _showAllMismatches;

    public bool HasMajorMismatches => !string.IsNullOrEmpty(MajorMismatchText);

    public bool HasMinorMismatches => !string.IsNullOrEmpty(MinorMismatchText);

    /// <summary>The section is reachable when either group has rows — minor-only shows can still be expanded.</summary>
    public bool HasMismatches => HasMajorMismatches || HasMinorMismatches;

    /// <summary>The collapsed summary shows only while there are minor rows and they are not expanded.</summary>
    public bool ShowMinorSummary => HasMinorMismatches && !ShowAllMismatches;

    /// <summary>The expanded minor list shows only while there are minor rows and they are expanded.</summary>
    public bool ShowMinorList => HasMinorMismatches && ShowAllMismatches;

    /// <summary>The View All / Show less button appears only when there are minor rows to reveal.</summary>
    public bool CanToggleMismatches => HasMinorMismatches;

    public string ToggleMismatchesText => ShowAllMismatches ? "Show less" : "View All";

    public long TrackedShowId { get; private set; }

    // The header block (issue 2). These were plain auto-properties with private setters and no change
    // notification, so LoadAsync assigned them after the page had already bound and the header stayed
    // blank (only the ObservableCollection seasons and the explicitly-notified progress/category props
    // rendered). They are now [ObservableProperty] fields — the source generator's setter raises
    // PropertyChanged, and each HasXxx toggle re-raises via NotifyPropertyChangedFor so the
    // IsVisible="{Binding HasXxx}" gates refresh with their backing field. Public property names are
    // unchanged (the XAML binds to PrimaryTitle, PosterUrl, …). Defaults preserved via field initializers.

    [ObservableProperty]
    private string _primaryTitle = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSecondaryTitle))]
    private string? _secondaryTitle;

    public bool HasSecondaryTitle => !string.IsNullOrEmpty(SecondaryTitle);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPoster))]
    private string? _posterUrl;

    public bool HasPoster => !string.IsNullOrEmpty(PosterUrl);

    [ObservableProperty]
    private string? _statusText;

    [ObservableProperty]
    private string _genresText = string.Empty;

    /// <summary>Network or web-channel name (mutually exclusive on the source, SPEC). Empty when neither set.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNetwork))]
    private string? _networkText;

    public bool HasNetwork => !string.IsNullOrEmpty(NetworkText);

    /// <summary>Premiere date (and weekday, when known) rendered for the header. Empty when no air date.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAirDate))]
    private string? _airDateText;

    public bool HasAirDate => !string.IsNullOrEmpty(AirDateText);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDescription))]
    private string? _description;

    public bool HasDescription => !string.IsNullOrEmpty(Description);

    [ObservableProperty]
    private string? _ratingText;

    // The show's current category (issue 8). Drives the category picker on the details page: the page
    // shows this and disables the matching button, and offers only Started / Later / Cancelled as move
    // targets — Finished is automatic (SPEC R5) and is never a manual target.
    private ShowCategory _category;

    /// <summary>The current category rendered for the picker header.</summary>
    public string CategoryText => _category.ToString();

    public bool IsStarted => _category == ShowCategory.Started;

    public bool IsLater => _category == ShowCategory.Later;

    public bool IsCancelled => _category == ShowCategory.Cancelled;

    private void RaiseCategoryChanged()
    {
        OnPropertyChanged(nameof(CategoryText));
        OnPropertyChanged(nameof(IsStarted));
        OnPropertyChanged(nameof(IsLater));
        OnPropertyChanged(nameof(IsCancelled));
    }

    // The external-link properties are plain auto-properties assigned in LoadAsync after the view has
    // bound, so their IsVisible gates need an explicit notification (the same reason the header block
    // became [ObservableProperty]). Without this the IMDb / Official site buttons never appear.
    private void RaiseLinksChanged()
    {
        OnPropertyChanged(nameof(TvmazeUrl));
        OnPropertyChanged(nameof(ImdbUrl));
        OnPropertyChanged(nameof(HasImdbLink));
        OnPropertyChanged(nameof(OfficialSiteUrl));
        OnPropertyChanged(nameof(HasOfficialSiteLink));
    }

    [ObservableProperty]
    private int _watchedRegular;

    [ObservableProperty]
    private int _totalRegular;

    [ObservableProperty]
    private bool _hasUnwatchedSpecials;

    public bool HasProgress => TotalRegular > 0;

    public double ProgressFraction => TotalRegular > 0 ? (double)WatchedRegular / TotalRegular : 0;

    public string ProgressText => TotalRegular > 0 ? $"{WatchedRegular} of {TotalRegular} watched" : "No episodes catalogued yet";

    /// <summary>TVmaze link built from the id at render (SPEC P5) — there is no URL column to read.</summary>
    public string TvmazeUrl { get; private set; } = string.Empty;

    /// <summary>IMDb link built from the id at render (SPEC P5); empty when the show has no IMDb id.</summary>
    public string ImdbUrl { get; private set; } = string.Empty;

    public bool HasImdbLink => !string.IsNullOrEmpty(ImdbUrl);

    /// <summary>
    /// The show's official-site URL. Unlike the TVmaze/IMDb links this is NOT derived from an id — it is
    /// authored catalog data with no derivation rule, so it is a stored column (SPEC P5 permits storing
    /// what cannot be derived). Empty when TVmaze has no official site for the show.
    /// </summary>
    public string OfficialSiteUrl { get; private set; } = string.Empty;

    public bool HasOfficialSiteLink => !string.IsNullOrEmpty(OfficialSiteUrl);

    // ---- The user's own rating (1-10), editable on the details page ----
    // A row of ten clickable stars. UserRating is the current value (null = unrated); each star fills when
    // its position is at or below it (StarFilledConverter in the view). Clicking a star sets that value,
    // except clicking the star at the CURRENT rating, which clears it — so the widget both sets and clears
    // without a separate control. Persists through SetShowUserRatingUseCase (null clears). Seeded from the
    // read model in LoadAsync.

    /// <summary>The user's current rating (1..10), or null when unrated. Drives which stars are filled.</summary>
    [ObservableProperty]
    private int? _userRating;

    /// <summary>
    /// Apply a star click. Clicking the star at the current rating clears the rating (a second click on the
    /// same value toggles it off); any other value — or a click while unrated — sets it.
    /// </summary>
    [RelayCommand]
    private Task SetRatingAsync(int value)
    {
        var target = UserRating == value ? (int?)null : value;
        return SaveUserRatingAsync(target);
    }

    private async Task SaveUserRatingAsync(int? value)
    {
        if (TrackedShowId == 0)
        {
            return;
        }

        IsBusy = true;
        StatusMessage = null;
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var setRating = scope.ServiceProvider.GetRequiredService<SetShowUserRatingUseCase>();
            // No ConfigureAwait(false): the catch/finally write bound properties (StatusMessage / IsBusy /
            // UserRating), so the continuation must stay on the UI thread (the Avalonia off-thread crash trap).
            var saved = await setRating.ExecuteAsync(TrackedShowId, value, CancellationToken.None);

            // Reflect the new value on the stars without a reload.
            UserRating = saved;
            StatusMessage = saved is { } r
                ? $"Your rating: {r}/10."
                : "Rating cleared.";
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    public ObservableCollection<SeasonGroupViewModel> Seasons { get; } = [];

    /// <summary>
    /// Special episodes (improvement 6), shown in a separate section at the bottom of the page rather than
    /// inline in the season grid. The partition is on <see cref="Episode.EpisodeType"/> — the Application
    /// read model's own flag (SPEC R4) — not on a source table the UI cannot read. Specials keep their
    /// watch toggle (a manual mark is allowed; R4 only forbids <em>auto</em>-marking), so they reuse the
    /// same <see cref="EpisodeRowViewModel"/> the grid uses.
    /// </summary>
    public ObservableCollection<EpisodeRowViewModel> Specials { get; } = [];

    public bool HasSpecials => Specials.Count > 0;

    /// <summary>
    /// Load the page from local rows. Called by the page with the id it received from Shell navigation.
    /// <paramref name="force"/> re-reads even when already loaded — the localized-title fetch changes
    /// <c>title_localized</c> and the R3 title split must refresh.
    /// </summary>
    public async Task LoadAsync(long trackedShowId, bool force = false)
    {
        if (!force && TrackedShowId == trackedShowId && Seasons.Count > 0)
        {
            return; // already loaded for this show
        }

        IsBusy = true;
        StatusMessage = null;
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var getDetails = scope.ServiceProvider.GetRequiredService<GetShowDetailsUseCase>();
            // No ConfigureAwait(false): the code after this await writes many bound properties
            // (PrimaryTitle / StatusText / …) and the catch/finally write StatusMessage / IsBusy. On the
            // FIRST load the view is not yet bound so an off-thread PropertyChanged is silently dropped,
            // but a force-reload (the akas / sync buttons) runs while bound — raising PropertyChanged off
            // the UI thread there throws "Call from invalid thread" and kills the process. Resuming on the
            // UI context makes every path safe.
            var details = await getDetails.ExecuteAsync(trackedShowId, CancellationToken.None);

            TrackedShowId = trackedShowId;
            var show = details.Show;

            var title = TitleRules.Select(show.Title, show.TitleLocalized);
            PrimaryTitle = title.Primary;
            SecondaryTitle = title.Secondary;
            PosterUrl = show.PosterMediumUrl ?? show.PosterUrl;
            // Human-readable status (improvement 3): ShowStatusDisplay is the single label table, so the
            // details page no longer renders the enum's PascalCase name ("InDevelopment"/"ToBeDetermined").
            StatusText = ShowStatusDisplay.Text(show.Status);
            // The genre line leads with the show's type (Scripted / Animated / …) when known — TVmaze's
            // own human-readable <c>type</c> string, shown as just another facet ahead of the genres.
            {
                var genreParts = new List<string>();
                if (!string.IsNullOrWhiteSpace(show.ShowType))
                {
                    genreParts.Add(show.ShowType!);
                }

                genreParts.AddRange(show.Genres.Select(g => g.Name));
                GenresText = string.Join(", ", genreParts);
            }
            // Network and web-channel are mutually exclusive on the source (SPEC); show whichever is set,
            // followed by the country. The country is the mapped column, which already carries the language
            // fallback (CountryRules, improvement 21) — a Korean Netflix drama with no broadcaster country
            // reads "Netflix · Korean" here exactly as its Discovery card does. The UI never re-derives it.
            {
                var networkParts = new List<string>();
                if (!string.IsNullOrWhiteSpace(show.NetworkName ?? show.WebchannelName))
                {
                    networkParts.Add(show.NetworkName ?? show.WebchannelName!);
                }

                if (!string.IsNullOrWhiteSpace(show.Country))
                {
                    networkParts.Add(show.Country!);
                }

                NetworkText = networkParts.Count > 0 ? string.Join("  ·  ", networkParts) : null;
            }
            // Air date: the premiere date, with the weekly schedule days appended when known.
            AirDateText = FormatAirDate(show);
            Description = show.Description;
            RatingText = show.RatingAverage?.ToString("0.0", CultureInfo.InvariantCulture);
            // Seed the user's own rating (which stars are filled) from the read model.
            UserRating = show.UserRating;
            WatchedRegular = show.WatchedRegular;
            TotalRegular = show.TotalRegular;
            HasUnwatchedSpecials = show.HasUnwatchedSpecials;
            _category = show.Category;

            // SPEC P5: the link is a function of the id, built here — the format lives in appsettings
            // (improvement 20) and ShowLinks is the single builder shared with the search-details page.
            TvmazeUrl = ShowLinks.Tvmaze(show.TvmazeId);
            ImdbUrl = ShowLinks.Imdb(show.ImdbId);
            // The official site is stored catalog data (not derived from an id), shown when TVmaze has one.
            OfficialSiteUrl = show.OfficialSite ?? string.Empty;
            // These four are plain properties assigned after the view has already bound, so the
            // IsVisible="{Binding HasImdbLink/HasOfficialSiteLink}" gates never re-evaluate on their own —
            // raise the notifications here or the IMDb / Official site buttons stay hidden (issue: a show
            // with both populated showed only the unconditional TVmaze button).
            RaiseLinksChanged();

            // Build the season/special view-models, then apply the collection mutations in one posted
            // batch on the UI thread. Seasons/Specials are bound to ItemsControls; the batch keeps the
            // swap atomic so a force-reload (the akas / sync buttons) never shows a half-cleared list.
            // (The read above now resumes on the UI context, so this is the same thread — the Post is a
            // harmless no-op hop that preserves the atomic-swap intent.)
            var newSeasons = new List<SeasonGroupViewModel>();
            var newSpecials = new List<EpisodeRowViewModel>();

            // Improvement 6 / ADR-034: the bottom Specials section holds only <em>insignificant</em>
            // specials (EpisodeType.InsignificantSpecial — TVmaze type_raw "insignificant_special").
            // Significant specials (EpisodeType.Special) stay in the season grid and count toward progress
            // like regulars; they carry a marker on their row instead. The flag is the read model's own
            // (SPEC R4), so the UI never reads episode_type from a source table. A season that holds only
            // insignificant specials (season 0) drops out of the main grid entirely; a season with a mix
            // keeps its counted episodes in the grid and contributes its filler to the bottom list.
            // Improvement 11: seasons render newest-first and episodes newest-first within each, via the
            // Domain comparators (SeasonEpisodeOrdering) — air date desc then number desc, nulls last.
            foreach (var season in details.Seasons.OrderBy(s => s, SeasonEpisodeOrdering.SeasonsDescending))
            {
                var seasonEpisodes = details.Episodes
                    .Where(e => e.SeasonId == season.Id)
                    .ToList();

                // Counted episodes (regulars + significant specials) stay in the grid; insignificant
                // specials go to the bottom section (ADR-034).
                var counted = seasonEpisodes
                    .Where(e => e.EpisodeType != EpisodeType.InsignificantSpecial)
                    .OrderBy(e => e, SeasonEpisodeOrdering.EpisodesDescending)
                    .ToList();

                foreach (var special in seasonEpisodes
                             .Where(e => e.EpisodeType == EpisodeType.InsignificantSpecial)
                             .OrderBy(e => e, SeasonEpisodeOrdering.EpisodesDescending))
                {
                    newSpecials.Add(new EpisodeRowViewModel(
                        special,
                        details.WatchedEpisodeIds.Contains(special.Id),
                        _scopeFactory,
                        TrackedShowId,
                        OnEpisodeToggled));
                }

                if (counted.Count == 0)
                {
                    continue; // a filler-only season (season 0) has no place in the counted grid
                }

                newSeasons.Add(new SeasonGroupViewModel(
                    season,
                    counted,
                    details.WatchedEpisodeIds,
                    details.ExpandedSeasons.GetValueOrDefault(season.Id),
                    _scopeFactory,
                    TrackedShowId,
                    OnEpisodeToggled));
            }

            MainThread.BeginInvokeOnMainThread(() =>
            {
                Seasons.Clear();
                Specials.Clear();
                foreach (var group in newSeasons)
                {
                    Seasons.Add(group);
                }

                foreach (var row in newSpecials)
                {
                    Specials.Add(row);
                }

                OnPropertyChanged(nameof(HasSpecials));
            });

            // Step 7.3 (ADR-031) / improvement 9: surface the recorded disagreements, split by kind. The
            // episode-set differences (imdb_only / tvmaze_only) are major and always shown; the high-volume
            // title / runtime / air_date differences are collapsed under a View All toggle. TVmaze is
            // authoritative, so each line shows what TVmaze kept and what IMDb offered that was rejected.
            var major = details.Mismatches.Where(IsMajorMismatch).ToList();
            var minor = details.Mismatches.Where(m => !IsMajorMismatch(m)).ToList();
            MajorMismatchText = FormatMismatches(major);
            MinorMismatchText = FormatMismatches(minor);
            MinorMismatchSummary = FormatMinorSummary(minor);
            ShowAllMismatches = false;
            RaiseMismatchesChanged();

            RaiseCategoryChanged();
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    // The header's air-date line: the premiere date, with the weekly schedule days appended when known.
    // Null when there is no premiere date at all (the section is then hidden).
    private static string? FormatAirDate(TrackedShow show)
    {
        if (show.PremiereDate is not { } premiere)
        {
            return null;
        }

        var text = premiere.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        return string.IsNullOrEmpty(show.ScheduleDays) ? text : $"{text} · {show.ScheduleDays}";
    }

    // One line per disagreement, labelled by kind. The (season, episode) label uses the denormalised
    // numbers so an imdb_only row (no episode row) still reads "S01E05".
    private static string? FormatMismatches(IReadOnlyList<EpisodeMismatch> mismatches)
    {
        if (mismatches.Count == 0)
        {
            return null;
        }

        static string Label(EpisodeMismatch m) =>
            $"S{m.SeasonNumber?.ToString(CultureInfo.InvariantCulture) ?? "?"}"
            + $"E{m.EpisodeNumber?.ToString(CultureInfo.InvariantCulture) ?? "?"}";

        static string? Show(string? value) => string.IsNullOrEmpty(value) ? "(none)" : value;

        return string.Join("\n", mismatches.Select(m => m.Kind switch
        {
            "imdb_only" => $"{Label(m)}: only in IMDb — \"{Show(m.ImdbValue)}\" (not added; TVmaze is the catalog)",
            "tvmaze_only" => $"{Label(m)}: only in TVmaze — \"{Show(m.TvmazeValue)}\" (kept)",
            "title" => $"{Label(m)}: title differs — TVmaze \"{Show(m.TvmazeValue)}\" kept, IMDb \"{Show(m.ImdbValue)}\"",
            "runtime" => $"{Label(m)}: runtime differs — TVmaze {Show(m.TvmazeValue)} min kept, IMDb {Show(m.ImdbValue)} min",
            "air_date_gap" => $"{Label(m)}: no air date (IMDb year {Show(m.ImdbValue)} only, not filled)",
            _ => $"{Label(m)}: {m.Kind}",
        }));
    }

    // Improvement 9: the major/minor split. Major = the episode-set differences (a show has episodes one
    // source lacks); everything else (title / runtime / air_date_gap, and any future field-level kind) is
    // minor and collapsed. Kept as one predicate so the partition is defined in exactly one place.
    private static bool IsMajorMismatch(EpisodeMismatch m) =>
        m.Kind is "imdb_only" or "tvmaze_only";

    // Collapsed one-line count of the minor differences, grouped by kind in a stable order, e.g.
    // "12 title / 40 runtime / 3 air-date differences". Null when there are none (nothing to summarise).
    private static string? FormatMinorSummary(IReadOnlyList<EpisodeMismatch> minor)
    {
        if (minor.Count == 0)
        {
            return null;
        }

        static string KindLabel(string kind) => kind switch
        {
            "title" => "title",
            "runtime" => "runtime",
            "air_date_gap" => "air-date",
            _ => kind,
        };

        var parts = minor
            .GroupBy(m => m.Kind)
            .OrderBy(g => g.Key switch { "title" => 0, "runtime" => 1, "air_date_gap" => 2, _ => 3 })
            .Select(g => $"{g.Count()} {KindLabel(g.Key)}");

        return string.Join(" / ", parts) + " differences";
    }

    private void RaiseMismatchesChanged()
    {
        OnPropertyChanged(nameof(HasMajorMismatches));
        OnPropertyChanged(nameof(HasMinorMismatches));
        OnPropertyChanged(nameof(HasMismatches));
        OnPropertyChanged(nameof(ShowMinorSummary));
        OnPropertyChanged(nameof(ShowMinorList));
        OnPropertyChanged(nameof(CanToggleMismatches));
        OnPropertyChanged(nameof(ToggleMismatchesText));
    }

    partial void OnShowAllMismatchesChanged(bool value) => RaiseMismatchesChanged();

    // Improvement 9: expand/collapse the minor differences. A reversible toggle — tapping again hides them.
    [RelayCommand]
    private void ToggleMismatches() => ShowAllMismatches = !ShowAllMismatches;

    // A toggle returns the show's new progress; refresh the header bar and the specials note. The category
    // may have moved to Finished (R5) — the user sees that on the next tab visit; here we surface the note.
    private void OnEpisodeToggled(ShowProgress progress)
    {
        WatchedRegular = progress.WatchedRegular;
        TotalRegular = progress.TotalRegular;
        HasUnwatchedSpecials = progress.HasUnwatchedSpecials;
        OnPropertyChanged(nameof(HasProgress));
        OnPropertyChanged(nameof(ProgressFraction));
        OnPropertyChanged(nameof(ProgressText));
    }

    /// <summary>
    /// Step 7.5's on-demand path: fetch this show's <c>/akas</c>, map the target-country pick through the
    /// mapper, and list every alternate. A single-show fetch, so no lease. R14: an unreachable TVmaze
    /// surfaces as the exception message — never a silent empty list.
    /// </summary>
    [RelayCommand]
    private async Task FetchLocalizedTitlesAsync()
    {
        if (TrackedShowId == 0)
        {
            return;
        }

        IsBusy = true;
        StatusMessage = null;
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var fetch = scope.ServiceProvider.GetRequiredService<FetchLocalizedTitlesUseCase>();
            // No ConfigureAwait(false): the lines after this await write bound properties (AkasText /
            // StatusMessage / IsBusy); resuming on the thread pool would raise PropertyChanged off the UI
            // thread and crash the Avalonia app (the same reason the shared card and Search avoid it).
            var result = await fetch.ExecuteAsync(TrackedShowId, CancellationToken.None);

            // List every aka (country + title) so the user can confirm the auto-pick is the right one.
            AkasText = result.Akas.Count == 0
                ? "TVmaze has no alternative titles for this show."
                : string.Join("\n", result.Akas.Select(a => $"{a.CountryCode}: {a.Name}"));

            // The pick may have just landed in title_localized — refresh the R3 title split.
            await LoadAsync(TrackedShowId, force: true);

            StatusMessage = result.PickedTitle is { Length: > 0 } picked
                ? $"Localized title set to \u201c{picked}\u201d."
                : "No title for the target country; title_localized left blank.";
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Improvement 7's on-demand path: re-fetch THIS show from TVmaze and re-run the mapper for it — the
    /// single-show analogue of the EpisodeRefresh job, through the same fetch path the add path uses. A
    /// single-show fetch, so no lease. R14: an unreachable TVmaze surfaces as the exception message and
    /// leaves the show's existing rows untouched (the mapper throws before writing). After a successful
    /// sync the page reloads so the new fields render.
    /// </summary>
    [RelayCommand]
    private async Task SyncAsync()
    {
        if (TrackedShowId == 0)
        {
            return;
        }

        IsBusy = true;
        StatusMessage = null;
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var sync = scope.ServiceProvider.GetRequiredService<SyncShowUseCase>();
            // No ConfigureAwait(false): the continuation writes bound properties (StatusMessage / IsBusy)
            // and must resume on the UI thread (see the akas command above).
            var result = await sync.ExecuteAsync(TrackedShowId, CancellationToken.None);

            // Re-read the page from the now-refreshed local rows (the same force-reload the akas button uses).
            await LoadAsync(TrackedShowId, force: true);

            StatusMessage = result.Changed
                ? "Synced — this show's data was updated."
                : "Synced — no changes from TVmaze.";
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task OpenTvmazeLinkAsync()
    {
        if (!string.IsNullOrEmpty(TvmazeUrl))
        {
            await Browser.OpenAsync(new Uri(TvmazeUrl));
        }
    }

    [RelayCommand]
    private async Task OpenImdbLinkAsync()
    {
        if (!string.IsNullOrEmpty(ImdbUrl))
        {
            await Browser.OpenAsync(new Uri(ImdbUrl));
        }
    }

    [RelayCommand]
    private async Task OpenOfficialSiteLinkAsync()
    {
        if (!string.IsNullOrEmpty(OfficialSiteUrl))
        {
            await Browser.OpenAsync(new Uri(OfficialSiteUrl));
        }
    }

    // ---- Category changes and untrack (issue 8) ----

    // The three manual move targets (SPEC R1). Finished is deliberately absent: it is assigned
    // automatically by the recompute (SPEC R5) and ChangeCategoryUseCase rejects it as a manual target.
    [RelayCommand]
    private Task MoveToStartedAsync() => ChangeCategoryAsync(ShowCategory.Started);

    [RelayCommand]
    private Task MoveToLaterAsync() => ChangeCategoryAsync(ShowCategory.Later);

    [RelayCommand]
    private Task MoveToCancelledAsync() => ChangeCategoryAsync(ShowCategory.Cancelled);

    // Move the show to another category (SPEC R6). Stamps category_changed_at, which is what makes R6
    // hold: a later recompute leaves the user's choice alone until the next watch change. The page stays
    // open and the picker reflects the new category; the list reloads when the user navigates back.
    private async Task ChangeCategoryAsync(ShowCategory target)
    {
        if (TrackedShowId == 0 || _category == target)
        {
            return;
        }

        IsBusy = true;
        StatusMessage = null;
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var change = scope.ServiceProvider.GetRequiredService<ChangeCategoryUseCase>();
            // No ConfigureAwait(false): the continuation raises the category-changed notification and writes
            // StatusMessage / IsBusy, which must happen on the UI thread.
            await change.ExecuteAsync(TrackedShowId, target, CancellationToken.None);

            _category = target;
            RaiseCategoryChanged();
            StatusMessage = $"Moved to {target}.";
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    // Untrack from the details page (issue 8): a logical delete (ADR-024), reusing the same use case the
    // list used to call. On success navigate back so the show disappears from its category tab (the list
    // reloads on its next appearance).
    [RelayCommand]
    private async Task UntrackAsync()
    {
        if (TrackedShowId == 0)
        {
            return;
        }

        IsBusy = true;
        StatusMessage = null;
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var untrack = scope.ServiceProvider.GetRequiredService<UntrackShowUseCase>();
            // No ConfigureAwait(false): the catch below writes bound properties (StatusMessage / IsBusy),
            // so the continuation must stay on the UI thread.
            await untrack.ExecuteAsync(TrackedShowId, CancellationToken.None);

            await Shell.Current.GoToAsync("..");
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
            IsBusy = false;
        }
    }
}
