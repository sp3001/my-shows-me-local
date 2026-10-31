using System.Collections.ObjectModel;
using System.Globalization;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Shows.App.Avalonia.Services;
using Shows.Application.UseCases;
using Shows.Domain;

namespace Shows.App.Avalonia.ViewModels;

/// <summary>
/// One season in a season/episode grid. Collapsed by default; expanding reveals its episodes (ADR-016)
/// and persists the state (R9). Used in two modes:
/// <list type="bullet">
/// <item><b>Details page</b> (improvement 11): always expanded, all episodes passed in and materialized
/// immediately, no filtering, no row removal on watch. The page's XAML renders <see cref="Episodes"/>
/// unconditionally, so <see cref="IsExpanded"/> only drives persistence there and no layout callback is
/// wired (the page is a ScrollView, not a CollectionView item, so it re-measures on its own).</item>
/// <item><b>List card</b> (improvement 4): the chevron toggles visibility, and a season that starts
/// collapsed materializes its (already-filtered) rows only on first expand — see
/// <see cref="CreateLazy"/>. An expanded season is built eagerly from the rows the card was given.</item>
/// </list>
/// </summary>
/// <remarks>
/// <para>
/// <b>Why no re-measure plumbing (issue 10).</b> The My Shows list renders its cards in a plain
/// <c>ScrollView</c> + <c>VerticalStackLayout</c>, not a <c>CollectionView</c>. A CollectionView on
/// Windows caches each item's measured height and would not re-measure a card when a nested
/// <c>BindableLayout</c> expanded or collapsed — neither <c>InvalidateMeasure</c> nor a collection
/// mutation fixed it (two failed attempts). A plain layout re-measures its children on every content
/// change, so a card grows/shrinks the instant a season opens. This VM therefore needs no layout callback
/// at all: it just mutates <see cref="Episodes"/> and the surrounding layout reacts.
/// </para>
/// <para>
/// <b>The header count (issue 11).</b> <see cref="CountText"/> is the number of displayable episodes
/// (unwatched, already-aired regulars — <see cref="InlineEpisodeRules"/>). The card computes it eagerly
/// for every season via a cheap aggregate, so a season with zero is dropped before it is ever shown, and
/// the count doubles as the header's "N left" text. Once a season's rows are materialized the count
/// tracks <see cref="Episodes"/> live, so it falls as the user watches episodes.
/// </para>
/// </remarks>
public sealed partial class SeasonGroupViewModel : ObservableObject
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly long _trackedShowId;
    private readonly IReadOnlySet<long> _watchedEpisodeIds;
    private readonly Action<ShowProgress> _onEpisodeToggled;
    private readonly bool _removeWatchedOnToggle;

    // Card mode only: invoked (on the main thread) when this season has no displayable episodes left — a
    // lazy load that found none, or the last inline row being watched — so the card can drop the group
    // entirely (issue 11). Null on the details page, which always shows every season.
    private readonly Action<SeasonGroupViewModel>? _onGroupEmptied;

    // Non-null only for a lazily-loaded (card-collapsed) season: the first expand calls it to fetch the
    // season's filtered rows. Null for every eagerly-built group (details page + card-expanded seasons).
    private readonly Func<Task<IReadOnlyList<Episode>>>? _lazyLoader;

    // Guards the lazy load so a collapse/re-expand reuses the rows instead of refetching. Always true for
    // eager groups (their rows are built in the constructor).
    private bool _rowsBuilt;

    // The eager displayable count for a collapsed season (computed by the card's aggregate before any rows
    // are loaded). Once rows are materialized the count is read live from Episodes instead, so this is
    // only consulted while _rowsBuilt is false. Null on the details page (no count shown there).
    private readonly int? _eagerCount;

    // When each watched episode was marked, for the details grid's "watched …" date. Null on the card, whose
    // inline rows are by construction all unwatched (improvement 4).
    private readonly IReadOnlyDictionary<long, DateTimeOffset>? _watchedAtByEpisodeId;

    /// <summary>
    /// Eager construction: <paramref name="episodes"/> are materialized into rows immediately. Used by the
    /// details page (all episodes, unfiltered) and by a card's already-expanded seasons (pre-filtered rows).
    /// </summary>
    /// <param name="removeWatchedOnToggle">
    /// Card mode only: when true, toggling a row to watched removes it from <see cref="Episodes"/> so the
    /// inline list stays "what is left to watch" (improvement 4). The details page leaves it false so a
    /// watched row remains (the user can un-watch it — improvement 11).
    /// </param>
    public SeasonGroupViewModel(
        Season season,
        IReadOnlyList<Episode> episodes,
        IReadOnlySet<long> watchedEpisodeIds,
        bool isExpanded,
        IServiceScopeFactory scopeFactory,
        long trackedShowId,
        Action<ShowProgress> onEpisodeToggled,
        bool removeWatchedOnToggle = false,
        Action<SeasonGroupViewModel>? onGroupEmptied = null,
        int? runtimeMinutes = null,
        IReadOnlyDictionary<long, DateTimeOffset>? watchedAtByEpisodeId = null)
    {
        Season = season;
        _scopeFactory = scopeFactory;
        _trackedShowId = trackedShowId;
        _watchedEpisodeIds = watchedEpisodeIds;
        _onEpisodeToggled = onEpisodeToggled;
        _removeWatchedOnToggle = removeWatchedOnToggle;
        _onGroupEmptied = onGroupEmptied;
        _watchedAtByEpisodeId = watchedAtByEpisodeId;
        _runtimeMinutes = runtimeMinutes;
        _isExpanded = isExpanded;

        Header = season.SeasonNumber == 0
            ? "Specials"
            : $"Season {season.SeasonNumber.ToString(CultureInfo.InvariantCulture)}";

        Episodes = new ObservableCollection<EpisodeRowViewModel>(episodes.Select(BuildRow));
        _rowsBuilt = true;
    }

    /// <summary>
    /// Lazy construction for a card's collapsed season: no rows are built now. The first time the user
    /// expands it, <paramref name="loader"/> fetches the season's filtered rows and they are materialized
    /// then (issue 7 — a collapsed season costs nothing until opened). <paramref name="displayableCount"/>
    /// is the card's eager count for this season, shown in the header until the rows arrive.
    /// </summary>
    public static SeasonGroupViewModel CreateLazy(
        Season season,
        Func<Task<IReadOnlyList<Episode>>> loader,
        int displayableCount,
        IReadOnlySet<long> watchedEpisodeIds,
        IServiceScopeFactory scopeFactory,
        long trackedShowId,
        Action<ShowProgress> onEpisodeToggled,
        Action<SeasonGroupViewModel>? onGroupEmptied = null,
        int? runtimeMinutes = null)
    {
        return new SeasonGroupViewModel(
            season, isExpanded: false, loader, displayableCount, watchedEpisodeIds, scopeFactory,
            trackedShowId, onEpisodeToggled, onGroupEmptied, runtimeMinutes);
    }

    // Private lazy constructor. Rows are deferred; the loader supplies them on first expand.
    private SeasonGroupViewModel(
        Season season,
        bool isExpanded,
        Func<Task<IReadOnlyList<Episode>>> loader,
        int displayableCount,
        IReadOnlySet<long> watchedEpisodeIds,
        IServiceScopeFactory scopeFactory,
        long trackedShowId,
        Action<ShowProgress> onEpisodeToggled,
        Action<SeasonGroupViewModel>? onGroupEmptied,
        int? runtimeMinutes)
    {
        Season = season;
        _scopeFactory = scopeFactory;
        _trackedShowId = trackedShowId;
        _watchedEpisodeIds = watchedEpisodeIds;
        _onEpisodeToggled = onEpisodeToggled;
        _removeWatchedOnToggle = true; // card mode: a watched inline row drops out (improvement 4)
        _onGroupEmptied = onGroupEmptied;
        _lazyLoader = loader;
        _isExpanded = isExpanded;
        _eagerCount = displayableCount;
        _runtimeMinutes = runtimeMinutes;

        Header = season.SeasonNumber == 0
            ? "Specials"
            : $"Season {season.SeasonNumber.ToString(CultureInfo.InvariantCulture)}";

        Episodes = [];
        _rowsBuilt = false;
    }

    public Season Season { get; }

    public long SeasonId => Season.Id;

    public string Header { get; }

    public ObservableCollection<EpisodeRowViewModel> Episodes { get; }

    /// <summary>
    /// The header's "N left" count (issue 11). While a collapsed season's rows are still deferred it shows
    /// the card's eager count; once the rows are materialized it tracks <see cref="Episodes"/> live so it
    /// falls as the user watches. Empty when there is nothing to show (the card drops such seasons anyway)
    /// or on the details page, where no count is surfaced.
    /// </summary>
    public string CountText
    {
        get
        {
            int? count = _rowsBuilt ? Episodes.Count : _eagerCount;
            return count is > 0 ? count.Value.ToString(CultureInfo.InvariantCulture) : string.Empty;
        }
    }

    /// <summary>The header's chevron: ▾ when expanded, ▸ when collapsed (the card's per-season toggle).</summary>
    public string Chevron => IsExpanded ? "\u25be" : "\u25b8";

    // The season's total runtime in minutes, from the read model's grouped aggregate — over the SAME
    // displayable episodes as CountText, so the two numbers describe the same set (what is left to watch).
    private readonly int? _runtimeMinutes;

    /// <summary>
    /// The header's total-duration text, e.g. "~3h 25m" — how long the season's listed episodes take.
    /// Empty when the provider has no runtime for them, so the header shows the count alone rather than a
    /// misleading zero.
    /// </summary>
    public string RuntimeText =>
        RuntimeFormat.Total(_runtimeMinutes) is { } total ? "~" + total : string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Chevron))]
    private bool _isExpanded;

    // Persist the expansion (R9) whenever the user toggles the header, and — for a lazily-loaded season —
    // materialize its rows the first time it opens. A persist failure is swallowed (expansion is a
    // convenience); the load failure surfaces as an empty season rather than crashing the grid.
    partial void OnIsExpandedChanged(bool value)
    {
        _ = PersistAsync(value);
        if (value && !_rowsBuilt && _lazyLoader is { } loader)
        {
            _rowsBuilt = true;
            _ = LoadRowsAsync(loader);
        }
    }

    [RelayCommand]
    private void Toggle() => IsExpanded = !IsExpanded;

    private EpisodeRowViewModel BuildRow(Episode episode) => new(
        episode,
        _watchedEpisodeIds.Contains(episode.Id),
        _scopeFactory,
        _trackedShowId,
        _onEpisodeToggled,
        _removeWatchedOnToggle ? OnRowBecameWatched : null,
        _watchedAtByEpisodeId is { } marks && marks.TryGetValue(episode.Id, out var watchedAt)
            ? watchedAt
            : null);

    // How long a just-watched row stays in the inline list before dropping out. The checkbox flips to
    // checked immediately (in EpisodeRowViewModel); the removal is deferred by this much so the user sees
    // their click register before the row animates away.
    private static readonly TimeSpan WatchedRowRemovalDelay = TimeSpan.FromSeconds(1.5);

    // The card's inline list is "what is left to watch": a row toggled to watched removes itself. Invoked
    // by EpisodeRowViewModel on the main thread (it marshals after its off-UI toggle continuation). The
    // row is left in place, still showing its checked box, for WatchedRowRemovalDelay before it is dropped.
    private void OnRowBecameWatched(EpisodeRowViewModel row)
    {
        _ = RemoveRowAfterDelayAsync(row);
    }

    private async Task RemoveRowAfterDelayAsync(EpisodeRowViewModel row)
    {
        await Task.Delay(WatchedRowRemovalDelay).ConfigureAwait(false);

        // Back on the UI thread to mutate the bound collection. Guard against a re-toggle during the delay:
        // if the row was un-watched again it must stay, so only remove it while it is still watched.
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (!row.IsWatched)
            {
                return;
            }

            Episodes.Remove(row);

            // The card shrank; refresh the header count. The card lives in a plain VerticalStackLayout, so
            // it re-measures on its own (issue 10). If nothing is left to watch, the season now has zero
            // displayable episodes and is dropped from the card (issue 11).
            OnPropertyChanged(nameof(CountText));
            if (Episodes.Count == 0)
            {
                _onGroupEmptied?.Invoke(this);
            }
        });
    }

    private async Task LoadRowsAsync(Func<Task<IReadOnlyList<Episode>>> loader)
    {
        IReadOnlyList<Episode> episodes;
        try
        {
            episodes = await loader().ConfigureAwait(false);
        }
        catch
        {
            return; // a failed lazy load leaves the season empty rather than throwing the list
        }

        var rows = episodes.Select(BuildRow).ToList();
        // The toggle/load continuation is off the UI thread (ConfigureAwait(false)); mutating the bound
        // collection must happen on the main thread.
        MainThread.BeginInvokeOnMainThread(() =>
        {
            if (rows.Count == 0)
            {
                // issue 11: a collapsed season that turns out to have no displayable episodes (all watched or
                // all unaired, after the improvement-4 filter) disappears rather than showing an empty header.
                _onGroupEmptied?.Invoke(this);
                return;
            }

            foreach (var row in rows)
            {
                Episodes.Add(row);
            }

            // Rows just appeared inside the card; the plain VerticalStackLayout re-measures on its own
            // (issue 10). Refresh the header count to the live row count.
            OnPropertyChanged(nameof(CountText));
        });
    }

    private async Task PersistAsync(bool value)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var setExpanded = scope.ServiceProvider.GetRequiredService<SetSeasonExpandedUseCase>();
            await setExpanded.ExecuteAsync(_trackedShowId, Season.Id, value, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch
        {
            // Expansion state is a convenience (R9); losing one write must not break the grid.
        }
    }
}

/// <summary>
/// One episode row with a watched toggle (SPEC R10 — bidirectional). The toggle runs
/// <see cref="ToggleWatchedEpisodeUseCase"/> and reports the new progress to the page so the header bar
/// updates; the auto-finish move (R5/R6) happens inside the use case, not here.
/// </summary>
public sealed partial class EpisodeRowViewModel : ObservableObject
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly Action<ShowProgress> _onToggled;

    // Card mode only: invoked (on the main thread) when a toggle marks this episode watched, so the owning
    // season can drop the row from the inline "what is left to watch" list (improvement 4). Null on the
    // details page, where a watched row stays so the user can un-watch it (improvement 11).
    private readonly Action<EpisodeRowViewModel>? _onBecameWatched;

    public EpisodeRowViewModel(
        Episode episode,
        bool isWatched,
        IServiceScopeFactory scopeFactory,
        long trackedShowId,
        Action<ShowProgress> onToggled,
        Action<EpisodeRowViewModel>? onBecameWatched = null,
        DateTimeOffset? watchedAt = null)
    {
        Episode = episode;
        _isWatched = isWatched;
        _watchedAt = watchedAt;
        _scopeFactory = scopeFactory;
        _onToggled = onToggled;
        _onBecameWatched = onBecameWatched;

        var title = TitleRules.Select(episode.Title ?? string.Empty, episode.TitleLocalized);
        PrimaryTitle = title.Primary;
        SecondaryTitle = title.Secondary;

        EpisodeType = episode.EpisodeType;
        // The marker distinguishes the four episode kinds at a glance (ADR-034). The season prefix is
        // shown only where there is no season header to supply it:
        //   Regular              -> "S02E05"    (season grid, header gives the season)
        //   Special (significant)-> "E05 ★"     (season grid — season is redundant, so episode + star)
        //   InsignificantSpecial -> "S02E05 ✧"  (season-less Specials section — full number + hollow star)
        //   Unknown              -> "? S02E05"  (vocabulary gap — visible, never silently a regular)
        NumberText = episode.EpisodeType switch
        {
            EpisodeType.Special => Mark(EpisodeNumberCore(episode), SpecialGlyph),
            EpisodeType.InsignificantSpecial => Mark(NumberCore(episode), InsignificantGlyph),
            EpisodeType.Unknown => UnknownGlyph + " " + NumberCore(episode),
            _ => NumberCore(episode),
        };
    }

    // Join a number core and a glyph, dropping the separator when the core is empty (a special with no
    // episode number renders as just the glyph, not " ★").
    private static string Mark(string core, string glyph) =>
        string.IsNullOrEmpty(core) ? glyph : core + " " + glyph;

    // The "S{season}E{number}" core, falling back to a bare "S{season}" when the number is unknown
    // (IMDb-legacy rows). Used where the season is needed (regulars, insignificant specials, unknowns).
    private static string NumberCore(Episode episode) =>
        episode.EpisodeNumber is { } n
            ? $"S{episode.SeasonNumber.ToString(CultureInfo.InvariantCulture)}"
                + $"E{n.ToString(CultureInfo.InvariantCulture)}"
            : $"S{episode.SeasonNumber.ToString(CultureInfo.InvariantCulture)}";

    // The "E{number}" core with no season — for a significant special, which sits in the season grid
    // under a season header that already names the season. Empty when the number is unknown.
    private static string EpisodeNumberCore(Episode episode) =>
        episode.EpisodeNumber is { } n
            ? $"E{n.ToString(CultureInfo.InvariantCulture)}"
            : string.Empty;

    /// <summary>The glyph marking a <em>significant</em> special (filled star).</summary>
    public const string SpecialGlyph = "★";

    /// <summary>The glyph marking an <em>insignificant</em> special (hollow star — distinct from the filled one).</summary>
    public const string InsignificantGlyph = "✧";

    /// <summary>The glyph shown before the number for an Unknown-type episode (a vocabulary gap).</summary>
    public const string UnknownGlyph = "?";

    // When this episode was marked watched, supplied by the details read model. Only a tracked show has
    // watch state, so the card's inline rows (which list only UNWATCHED episodes) never carry one. Mutable:
    // a toggle in this row updates it so the date appears (or clears) without a page reload — the use case
    // stamps UtcNow, so the row stamps the same instant it just wrote.
    private DateTimeOffset? _watchedAt;

    public Episode Episode { get; }

    public long EpisodeId => Episode.Id;

    public string NumberText { get; }

    /// <summary>
    /// The watched date shown beside the air date on the details grid (tracked shows only). The date, not
    /// the instant: the user cares which day they watched it, and the time would only widen the row.
    /// </summary>
    public string? WatchedAtText =>
        IsWatched && _watchedAt is { } at ? at.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : null;

    /// <summary>TVmaze's rating for this episode, e.g. "8.1"; null when unrated, so the chip hides.</summary>
    public string? RatingText => Episode.RatingAverage?.ToString("0.0", CultureInfo.InvariantCulture);

    /// <summary>The episode's runtime, e.g. "42 min"; null when the provider has none.</summary>
    public string? RuntimeText => RuntimeFormat.Episode(Episode.RuntimeMinutes);

    /// <summary>
    /// The synopsis, capped to one trimmed line for the row. The full text (plus the still) opens in the
    /// row's info popup, so the row itself stays two lines tall whatever the source length is.
    /// </summary>
    public string DescriptionText
    {
        get
        {
            const int max = 160;
            var text = Episode.Description;
            if (string.IsNullOrWhiteSpace(text))
            {
                return string.Empty;
            }

            text = text.Trim();
            if (text.Length <= max)
            {
                return text;
            }

            var cut = text.LastIndexOf(' ', max);
            return (cut > 0 ? text[..cut] : text[..max]).TrimEnd() + "…";
        }
    }

    public bool HasDescription => DescriptionText.Length > 0;

    /// <summary>
    /// The untruncated synopsis for the info popup. The row shows <see cref="DescriptionText"/> (one line)
    /// while the popup shows this in full — that is the whole point of the popup, so the two must not share
    /// a property.
    /// </summary>
    public string? FullDescription => string.IsNullOrWhiteSpace(Episode.Description) ? null : Episode.Description!.Trim();

    /// <summary>The episode still for the info popup; null when TVmaze has none.</summary>
    public string? ImageUrl => Episode.ImageMediumUrl ?? Episode.ImageUrl;

    public bool HasImage => !string.IsNullOrEmpty(ImageUrl);

    /// <summary>
    /// Whether the row gets an info button at all — only when there is something to show in the popup, so a
    /// show whose episodes have no synopsis or still does not grow a button that opens an empty box.
    /// </summary>
    public bool HasInfo => HasDescription || HasImage;

    public string PrimaryTitle { get; }

    public string? SecondaryTitle { get; }

    public bool HasSecondaryTitle => !string.IsNullOrEmpty(SecondaryTitle);

    /// <summary>The episode's kind, exposed so the XAML can style each type distinctly (ADR-034).</summary>
    public EpisodeType EpisodeType { get; }

    /// <summary>
    /// True for a <em>significant</em> special (counts toward progress, flagged in the grid). The
    /// details-page XAML shows a "special" tag when this is set. Insignificant specials live in their
    /// own section and are not flagged here.
    /// </summary>
    public bool IsSpecial => EpisodeType == EpisodeType.Special;

    public string? AirDateText => Episode.AirDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WatchedAtText))]
    private bool _isWatched;

    [ObservableProperty]
    private bool _isBusy;

    [RelayCommand]
    private async Task ToggleWatchedAsync()
    {
        IsBusy = true;
        ShowProgress? progress = null;
        var ok = false;
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var toggle = scope.ServiceProvider.GetRequiredService<ToggleWatchedEpisodeUseCase>();
            progress = await toggle.ExecuteAsync(EpisodeId, CancellationToken.None).ConfigureAwait(false);
            ok = true;
        }
        catch
        {
            // A failed toggle leaves the row's watched state unchanged; only IsBusy is reset (below).
        }

        // The toggle completes on a background thread (ConfigureAwait(false)); IsWatched, the page-header
        // callback, and IsBusy are all bound, so every one must be applied on the main thread — writing a
        // bound property off the UI thread throws "Call from invalid thread" and crashes the Avalonia app.
        MainThread.BeginInvokeOnMainThread(() =>
        {
            if (ok && progress is not null)
            {
                // The timestamp first, so the PropertyChanged raised by IsWatched recomputes WatchedAtText
                // against the new state rather than the old one.
                _watchedAt = IsWatched ? null : DateTimeOffset.UtcNow;
                IsWatched = !IsWatched;
                _onToggled(progress);

                // Card mode: a row that just became watched leaves the inline list (improvement 4). The
                // details page passes no callback, so its watched rows stay.
                if (IsWatched && _onBecameWatched is { } onWatched)
                {
                    onWatched(this);
                }
            }

            IsBusy = false;
        });
    }
}
