using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Shows.Application.UseCases;
using Shows.Domain;

namespace Shows.App.Avalonia.ViewModels;

/// <summary>
/// The four category tabs over the user's tracked shows (SPEC F4). One ViewModel drives all four tabs by
/// switching the selected category and reloading — the repository already filters
/// <c>deleted_at IS NULL</c> (SPEC R8), so the VM never re-filters.
/// </summary>
/// <remarks>
/// <para>
/// The category selector is a row of tappable links (improvement 1), not a dropdown: each category is
/// bound to <see cref="SelectCategoryCommand"/> and highlights itself when active. The four categories
/// are a closed set (SPEC R1), so the links are fixed, not data-driven.
/// </para>
/// <para>
/// <b>Sort and filter are VM-side</b> (improvements 2 and 8). The lists are small — hundreds of shows at
/// most — so re-ordering and re-filtering the already-loaded cards in memory is cheaper than a new query
/// per keystroke, and keeps the repository read a single indexed pass. The loaded cards live in
/// <see cref="_allCards"/>; <see cref="Shows"/> is the filtered + sorted projection rebuilt from it.
/// </para>
/// </remarks>
public sealed partial class TrackingViewModel : ObservableObject
{
    private readonly IServiceScopeFactory _scopeFactory;

    // The full, unfiltered, unsorted load for the current category. Shows is rebuilt from this whenever
    // the filter text or sort changes, so a keystroke never re-hits the database.
    private List<TrackedShowCardViewModel> _allCards = [];

    // The Upcoming view's raw rows (improvement 4, regrouped by date), loaded by a separate batched read
    // when the Upcoming tab is selected. Kept as the flat episode list so a title filter can re-group into
    // date blocks without re-hitting the database. Kept apart from _allCards (a different shape entirely).
    private IReadOnlyList<UpcomingEpisode> _allUpcoming = [];

    // The clock the upcoming read was filtered by, kept so re-grouping after a filter uses the same
    // "this week" boundary as the load (a filter must not shift which block an episode lands in).
    private DateOnly _upcomingToday;

    // UI memoization (Started list lag): the card VMs built for each category, kept across tab switches. On
    // the next load of a category the list still reads the data fresh (so it can tell whether anything
    // changed), but reuses a cached card VM whose freshly-read data signature is unchanged instead of
    // rebuilding its season-group VMs — and, when the resulting list is identical, skips mutating the bound
    // collection so the realized views are reused rather than re-created. This is a UI cache keyed on data
    // already loaded for the list, not a DB fact: nothing is skipped in the read, only in the rebuild.
    private readonly Dictionary<ShowCategory, Dictionary<long, TrackedShowCardViewModel>> _cardCache = new();

    // The per-show last-aired date from the Started batch read, cached so the Started "sort by last aired"
    // (improvement 12) orders in memory without a second query. Empty for non-Started categories.
    private IReadOnlyDictionary<long, DateOnly> _lastAiredByShow = EmptyLastAired;

    private static readonly IReadOnlyDictionary<long, DateOnly> EmptyLastAired =
        new Dictionary<long, DateOnly>();

    private static readonly IReadOnlyDictionary<long, CardSeasons> EmptyCardSeasons =
        new Dictionary<long, CardSeasons>();

    // The Preferences key under which the chosen Started sort is persisted across relaunches (improvement
    // 12). MAUI Preferences (not the DB) — a sort choice is a UI preference, not user data (SPEC P4).
    private const string SortPreferenceKey = "started_sort";

    [ObservableProperty]
    private CategoryTab _selectedCategory;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string? _statusMessage;

    /// <summary>The visible cards for the current category, after filter and sort are applied.</summary>
    /// <remarks>
    /// Bound to the <b>CollectionView</b>, which is shown for the non-Started categories (Later/Cancelled/
    /// Finished). Those cards have no inline seasons, so they never expand — the CollectionView's cached
    /// item height (ADR-032) is never wrong for them, and its virtualization is what keeps the 200+-item
    /// Later/Finished lists opening instantly. <see cref="StartedShows"/> is empty while this is populated.
    /// </remarks>
    public ObservableCollection<TrackedShowCardViewModel> Shows { get; } = [];

    /// <summary>
    /// The cards for the <b>Started</b> category only, bound to the re-measuring
    /// <c>ScrollView</c> + <c>VerticalStackLayout</c> (ADR-016/ADR-032). Started is the one category whose
    /// cards expand inline seasons, which a CollectionView cannot re-measure — so it uses a plain layout
    /// that re-measures on expand. Started is also the smallest list (78 shows), so realizing all its cards
    /// synchronously is fast. This collection is empty for every other category, so the hidden layout
    /// realizes nothing.
    /// </summary>
    public ObservableCollection<TrackedShowCardViewModel> StartedShows { get; } = [];

    /// <summary>
    /// The Started shows with <b>nothing left to watch</b> — every already-aired regular is watched, so the
    /// card has no displayable season (improvement 4). Rendered in a separate "Waiting for new episodes"
    /// section below <see cref="StartedShows"/>, and only when <see cref="ShowWaitingShows"/> is on (off by
    /// default, so these shows are hidden unless the user asks for them). A show lands here when its batched
    /// card read yields zero displayable seasons — the same predicate the card uses to drop empty seasons.
    /// </summary>
    public ObservableCollection<TrackedShowCardViewModel> StartedWaitingShows { get; } = [];

    /// <summary>
    /// The Upcoming tab's date blocks (improvement 4, regrouped by date): the Started shows'
    /// unknown-or-future episodes grouped into a highlighted "This Week" block (hidden when empty) and an
    /// unlabelled "Other" block, each holding day groups. Bound to a dedicated list shown only when
    /// <see cref="IsUpcoming"/>. Populated by a separate batched read, then grouped here.
    /// </summary>
    public ObservableCollection<UpcomingBlockViewModel> UpcomingShows { get; } = [];

    /// <summary>True when the Upcoming view tab is selected (improvement 4). Drives which list control shows.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsStartedView))]
    [NotifyPropertyChangedFor(nameof(IsSortable))]
    private bool _isUpcoming;

    /// <summary>
    /// True when the Started <b>tracker</b> tab is showing — Started but not the Upcoming view. The sort
    /// control, the Waiting checkbox, and the Started list all key off this so they do not appear on Upcoming.
    /// </summary>
    public bool IsStartedView => IsStarted && !IsUpcoming;

    /// <summary>
    /// Improvement 4: whether the "Waiting for new episodes" section is shown. Off by default — a user who
    /// has finished everything aired so far does not want those shows cluttering the tracker until there is
    /// something new. The section is only actually visible when this is on AND there are waiting shows.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowWaitingSection))]
    private bool _showWaitingShows;

    /// <summary>True when the "Waiting for new episodes" header + section should render.</summary>
    public bool ShowWaitingSection => ShowWaitingShows && StartedWaitingShows.Count > 0;

    /// <summary>
    /// The tabs: the four categories (SPEC R1 — a closed set, there is no fifth) plus an <b>Upcoming</b>
    /// view inserted after Started. Upcoming is not a category — it is a read-only view over the Started
    /// shows filtered to their unknown-or-future episodes (improvement 4), so its tab carries
    /// <c>IsUpcoming</c> and a <see cref="ShowCategory"/> of Started that is never used to query a category.
    /// </summary>
    public IReadOnlyList<CategoryTab> CategoryTabs { get; } =
    [
        new(ShowCategory.Started, "Started"),
        new(ShowCategory.Started, "Upcoming", IsUpcoming: true),
        new(ShowCategory.Later, "Later"),
        new(ShowCategory.Cancelled, "Cancelled"),
        new(ShowCategory.Finished, "Finished"),
    ];

    /// <summary>The sort options offered for the Started list (improvement 2, extended by improvement 12).</summary>
    public IReadOnlyList<SortOption> SortOptions { get; } =
    [
        new(ShowSortOrder.Name, "Name"),
        new(ShowSortOrder.AirDate, "Air date"),
        new(ShowSortOrder.LastAired, "Last aired"),
        new(ShowSortOrder.LastAdded, "Last added"),
    ];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsStarted))]
    [NotifyPropertyChangedFor(nameof(IsStartedView))]
    [NotifyPropertyChangedFor(nameof(IsSortable))]
    private ShowCategory _currentCategory;

    [ObservableProperty]
    private SortOption _selectedSortOption;

    [ObservableProperty]
    private string _filterText = string.Empty;

    public TrackingViewModel(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory;
        _selectedCategory = CategoryTabs[0];
        _selectedSortOption = RestoreSortOption();
    }

    // Read the persisted Started sort (improvement 12). An unknown or missing value falls back to Name.
    // Field assignment here does not fire OnSelectedSortOptionChanged, so it does not re-persist on launch.
    private SortOption RestoreSortOption()
    {
        var stored = Preferences.Default.Get(SortPreferenceKey, (int)ShowSortOrder.Name);
        return SortOptions.FirstOrDefault(o => o.Order == (ShowSortOrder)stored, SortOptions[0]);
    }

    /// <summary>The active sort order, derived from the selected option (improvement 2).</summary>
    public ShowSortOrder SelectedSort => SelectedSortOption.Order;

    /// <summary>True when the Started tab is showing — the only category that offers a sort control.</summary>
    public bool IsStarted => CurrentCategory == ShowCategory.Started;

    /// <summary>
    /// True when the current list offers the sort control: the Started tracker and the Later list. Later
    /// shares Started's sort (Name / Air date / Last aired); the last-aired key is fetched for it too
    /// (see <see cref="LoadCoreAsync"/>). Cancelled/Finished and the Upcoming view stay unsorted.
    /// </summary>
    public bool IsSortable => (IsStarted && !IsUpcoming) || CurrentCategory == ShowCategory.Later;

    /// <summary>True while a name filter is active, so the list can show a "no matches" message.</summary>
    public bool HasFilterText => !string.IsNullOrWhiteSpace(FilterText);

    partial void OnSelectedCategoryChanged(CategoryTab value)
    {
        // The Upcoming tab shares Started's category but is a distinct view (improvement 4). Set the flag
        // before loading so LoadCoreAsync routes to the upcoming read and the XAML shows the right control.
        IsUpcoming = value.IsUpcoming;
        _ = LoadAsync();
    }

    partial void OnSelectedSortOptionChanged(SortOption value)
    {
        // Persist the chosen sort across relaunches (improvement 12). MAUI Preferences — a sort choice is a
        // UI preference, not user data, so it does not belong in the DB (SPEC P4).
        Preferences.Default.Set(SortPreferenceKey, (int)value.Order);
        ApplyFilterAndSort();
    }

    // Toggling the "Waiting for new episodes" checkbox re-runs the split so the section fills or empties
    // without a reload (improvement 4).
    partial void OnShowWaitingShowsChanged(bool value) => ApplyFilterAndSort();

    partial void OnFilterTextChanged(string value)
    {
        OnPropertyChanged(nameof(HasFilterText));
        ApplyFilterAndSort();
    }

    /// <summary>Category link tap (improvement 1): select the tab, which reloads it.</summary>
    [RelayCommand]
    private void SelectCategory(CategoryTab tab) => SelectedCategory = tab;

    [RelayCommand]
    private Task LoadAsync() => LoadCoreAsync();

    public Task InitializeAsync() => LoadCoreAsync();

    private async Task LoadCoreAsync()
    {
        IsBusy = true;
        StatusMessage = null;
        try
        {
            // The Upcoming view (improvement 4) is a different shape — per-show future/unknown episodes, not
            // category cards — so it has its own batched read and its own card list. Handled here and returned
            // early; the finally still clears IsBusy.
            if (IsUpcoming)
            {
                await LoadUpcomingAsync().ConfigureAwait(false);
                return;
            }

            var category = SelectedCategory.Category;
            // Only Started is an episode watch tracker, so only its cards load the inline season/episode
            // list. Later/Cancelled/Finished render poster/title/progress only — skipping the per-card
            // season query is what makes those (often hundreds of shows) lists load fast.
            var showSeasons = category == ShowCategory.Started;

            List<TrackedShowCardViewModel> cards;
            await using (var scope = _scopeFactory.CreateAsyncScope())
            {
                var overview = scope.ServiceProvider.GetRequiredService<GetCategoryOverviewUseCase>();
                var result = await overview.ExecuteAsync(category, CancellationToken.None)
                    .ConfigureAwait(false);

                // The data is always read fresh — that is how we know whether anything changed. The cache
                // only saves the REBUILD: a card whose freshly-read data signature matches its cached VM is
                // reused as-is (its season groups are already built), instead of constructing a new VM and
                // re-materializing every season group. UI memoization, keyed on data already loaded here.
                var previous = _cardCache.TryGetValue(category, out var prior) ? prior : null;
                var nextCache = new Dictionary<long, TrackedShowCardViewModel>(result.Shows.Count);

                IReadOnlyDictionary<long, CardSeasons> byShow = EmptyCardSeasons;
                if (showSeasons && result.Shows.Count > 0)
                {
                    var batch = scope.ServiceProvider.GetRequiredService<GetStartedCardsUseCase>();
                    var today = DateOnly.FromDateTime(DateTime.UtcNow);
                    var ids = result.Shows.Select(s => s.Id).ToList();
                    var batchResult = await batch.ExecuteAsync(ids, today, CancellationToken.None)
                        .ConfigureAwait(false);
                    byShow = batchResult.ByShow;
                    _lastAiredByShow = batchResult.LastAiredByShow;
                }
                else if (category == ShowCategory.Later && result.Shows.Count > 0)
                {
                    // Later offers the same sort as Started (incl. "last aired") but skips the heavy
                    // per-card batch, so fetch only the sort key — one grouped query.
                    var lastAired = scope.ServiceProvider.GetRequiredService<GetLastAiredByShowUseCase>();
                    var today = DateOnly.FromDateTime(DateTime.UtcNow);
                    var ids = result.Shows.Select(s => s.Id).ToList();
                    _lastAiredByShow = await lastAired.ExecuteAsync(ids, today, CancellationToken.None)
                        .ConfigureAwait(false);
                }
                else
                {
                    _lastAiredByShow = EmptyLastAired;
                }

                cards = new List<TrackedShowCardViewModel>(result.Shows.Count);
                foreach (var show in result.Shows)
                {
                    var cardSeasons = showSeasons && byShow.TryGetValue(show.Id, out var cs) ? cs : null;
                    var signature = TrackedShowCardViewModel.ComputeSignature(show, cardSeasons);

                    // Reuse the cached card only if it exists, was built for this category's season mode, and
                    // renders exactly the same data. A changed signature (new episode, a watch toggle, a
                    // persisted expansion, a job's edit) falls through to a fresh build.
                    if (previous is not null
                        && previous.TryGetValue(show.Id, out var cached)
                        && cached.DataSignature == signature)
                    {
                        cards.Add(cached);
                        nextCache[show.Id] = cached;
                        continue;
                    }

                    var card = new TrackedShowCardViewModel(show, _scopeFactory, showSeasons);
                    // Subscribe once per card the list owns (improvement 18). A cached card reused above is
                    // already subscribed from when it was first built; the card and this list share a
                    // lifetime (the card cache owns the card), so no unsubscribe is needed.
                    card.PinToggled += OnCardPinToggled;
                    if (cardSeasons is not null)
                    {
                        // Build the season groups now, before the card is bound to any view — a brand-new
                        // card's Seasons collection is not yet observed, so mutating it here is safe and
                        // leaves HasSeasons correct for the split below. Only changed cards reach here, so an
                        // unchanged list does zero season-group rebuilds.
                        card.ApplyBatchedSeasons(cardSeasons);
                    }

                    cards.Add(card);
                    nextCache[show.Id] = card;
                }

                _cardCache[category] = nextCache;
                StatusMessage = result.TotalCount == 0 ? $"Nothing in {SelectedCategory.Display} yet." : null;
            }

            CurrentCategory = category;
            _allCards = cards;
            ApplyFilterAndSort();
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

    // Loads the Upcoming view (improvement 4, regrouped by date): Started shows' unknown-or-future episodes
    // as one flat soonest-first list. One batched read; the list is kept raw so a title filter re-groups it
    // into date blocks without re-querying.
    private async Task LoadUpcomingAsync()
    {
        IReadOnlyList<UpcomingEpisode> rows;
        await using (var scope = _scopeFactory.CreateAsyncScope())
        {
            var useCase = scope.ServiceProvider.GetRequiredService<GetUpcomingEpisodesUseCase>();
            var today = DateOnly.FromDateTime(DateTime.UtcNow);
            rows = await useCase.ExecuteAsync(today, CancellationToken.None).ConfigureAwait(false);
            _upcomingToday = today;
        }

        // CurrentCategory = Started so IsStarted is true, but IsUpcoming is also true, so IsStartedView is
        // false — the sort control and Waiting checkbox stay hidden and the upcoming list control shows.
        CurrentCategory = ShowCategory.Started;
        _allUpcoming = rows;
        ApplyFilterAndSort();
    }

    // Rebuilds the visible collection from the loaded cards: filter by title (improvement 8), then sort
    // (improvement 2). Sort is Name for every category; Air date is only reachable from Started because
    // the control is hidden elsewhere, but the comparer is harmless regardless of category.
    private void ApplyFilterAndSort()
    {
        // The Upcoming view (improvement 4, regrouped by date) is a different shape — a flat episode list
        // grouped into date blocks. Filter by show title, then group the survivors into This Week / Other
        // blocks and day groups. The rows arrive soonest-first, so grouping is a single ordered pass.
        if (IsUpcoming)
        {
            IEnumerable<UpcomingEpisode> rows = _allUpcoming;
            var upcomingFilter = FilterText?.Trim();
            if (!string.IsNullOrEmpty(upcomingFilter))
            {
                rows = rows.Where(r =>
                    r.Show.Title.Contains(upcomingFilter, StringComparison.OrdinalIgnoreCase)
                    || (r.Show.TitleLocalized?.Contains(upcomingFilter, StringComparison.OrdinalIgnoreCase) ?? false));
            }

            var blocks = GroupUpcomingByDate(rows.ToList(), _upcomingToday);
            StatusMessage = blocks.Count == 0 ? "No upcoming episodes right now." : null;
            SyncTo(UpcomingShows, blocks);
            return;
        }

        IEnumerable<TrackedShowCardViewModel> cards = _allCards;

        var filter = FilterText?.Trim();
        if (!string.IsNullOrEmpty(filter))
        {
            cards = cards.Where(c =>
                c.PrimaryTitle.Contains(filter, StringComparison.OrdinalIgnoreCase)
                || (c.SecondaryTitle?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false));
        }

        cards = SelectedSort switch
        {
            ShowSortOrder.AirDate => cards.OrderByDescending(c => c.Show.PremiereDate)
                .ThenBy(c => c.PrimaryTitle, StringComparer.OrdinalIgnoreCase),
            // Improvement 12: most recent already-aired episode first. A show with no aired episode (absent
            // from the batch's last-aired map) sorts last; ties break by name.
            ShowSortOrder.LastAired => cards
                .OrderByDescending(c => _lastAiredByShow.TryGetValue(c.TrackedShowId, out var d) ? d : DateOnly.MinValue)
                .ThenBy(c => c.PrimaryTitle, StringComparer.OrdinalIgnoreCase),
            // "Last Added": newest tracked shows first. The surrogate id is a time-ordered key (ADR-027,
            // SCHEMA §1a — ms-since-epoch high bits), so id desc is added-at desc without reading added_at.
            ShowSortOrder.LastAdded => cards.OrderByDescending(c => c.TrackedShowId),
            _ => cards.OrderBy(c => c.PrimaryTitle, StringComparer.OrdinalIgnoreCase),
        };

        // Materialize once: the routing below compares the desired contents against the bound collection
        // element-by-element, so it needs an indexable list, not a lazy LINQ sequence.
        var ordered = cards.ToList();

        // Improvement 18: pinned shows float to the top under EVERY sort. A stable partition keeps the
        // active sort's order within the pinned block and within the unpinned block, so pinning only lifts
        // a show above the sort — it never scrambles the order the user chose. Only Started cards can be
        // pinned through the UI, but the partition is harmless elsewhere (no other card reports IsPinned).
        if (ordered.Any(c => c.IsPinned))
        {
            ordered = [.. ordered.Where(c => c.IsPinned), .. ordered.Where(c => !c.IsPinned)];
        }

        // Route to exactly one list control so the hidden one realizes nothing (ADR-032): Started uses the
        // re-measuring VerticalStackLayout; the other categories use the virtualized CollectionView (Shows).
        // Only one control is ever non-empty. Sync each collection to its desired contents only if they
        // actually differ — mutating a bound collection (even Clear+re-Add of the same items) forces the
        // VerticalStackLayout to tear down and re-realize every card, which is the residual lag on an
        // unchanged tab switch. When the contents already match, the realized views are left untouched.
        var started = CurrentCategory == ShowCategory.Started;

        if (!started)
        {
            SyncTo(Shows, ordered);
            StartedShows.Clear();
            StartedWaitingShows.Clear();
            return;
        }

        // Started splits into two sections (improvement 4): shows with something left to watch, and shows
        // whose already-aired episodes are all watched ("Waiting for new episodes"). A card has no displayable
        // season once its batched read dropped every empty season, so HasSeasons is exactly "has something to
        // watch". The waiting section is only populated when the user turned the checkbox on.
        var active = new List<TrackedShowCardViewModel>();
        var waiting = new List<TrackedShowCardViewModel>();
        foreach (var card in ordered)
        {
            if (card.HasSeasons)
            {
                active.Add(card);
            }
            else if (ShowWaitingShows)
            {
                waiting.Add(card);
            }
        }

        Shows.Clear();
        SyncTo(StartedShows, active);
        SyncTo(StartedWaitingShows, waiting);
        OnPropertyChanged(nameof(ShowWaitingSection));
    }

    // A card's pin flag changed (improvement 18). The card VM instance in _allCards already reflects the new
    // flag (the card raised this after updating itself on the main thread), so re-running the projection
    // re-partitions pinned-first and the card moves to (or out of) the top block. No reload — the data is
    // unchanged apart from the flag the card already carries.
    private void OnCardPinToggled(object? sender, EventArgs e) => ApplyFilterAndSort();

    // Groups the (already soonest-first) upcoming rows into the two date blocks the tab shows: a highlighted
    // "This Week" (air date from today through today + 7 days, dropped when empty) and an unlabelled
    // "Other" (everything later, plus the undated rows the use case sorted last). Within a block, rows are
    // grouped by their exact air date into day groups; the rows arrive ordered, so a running dictionary
    // preserves first-seen order. An undated episode forms a single "Unknown date" day in "Other".
    private static List<UpcomingBlockViewModel> GroupUpcomingByDate(
        IReadOnlyList<UpcomingEpisode> rows, DateOnly today)
    {
        var weekEnd = today.AddDays(7);

        // Two ordered day buckets keyed by air date (null = the undated bucket). A Dictionary preserves the
        // insertion order of keys it has never had a collision on, but to be explicit we keep the day list
        // separately so the soonest-first order from the use case is what the UI sees.
        var thisWeek = new List<UpcomingDayViewModel>();
        var other = new List<UpcomingDayViewModel>();
        var dayByDate = new Dictionary<DateOnly, UpcomingDayViewModel>();
        UpcomingDayViewModel? unknownDay = null;

        foreach (var row in rows)
        {
            var date = row.Episode.AirDate;

            UpcomingDayViewModel day;
            if (date is { } d)
            {
                if (!dayByDate.TryGetValue(d, out day!))
                {
                    day = new UpcomingDayViewModel(d);
                    dayByDate[d] = day;
                    // A dated episode is "This Week" only when its date falls in [today, today + 7].
                    (d >= today && d <= weekEnd ? thisWeek : other).Add(day);
                }
            }
            else
            {
                // Undated: one shared "Unknown date" day, placed in "Other" (it has no date to be "this week").
                unknownDay ??= new UpcomingDayViewModel(null);
                if (!other.Contains(unknownDay))
                {
                    other.Add(unknownDay);
                }

                day = unknownDay;
            }

            day.Rows.Add(new UpcomingEpisodeRowViewModel(row));
        }

        var blocks = new List<UpcomingBlockViewModel>();
        if (thisWeek.Count > 0)
        {
            blocks.Add(new UpcomingBlockViewModel("This Week", isHighlighted: true, thisWeek));
        }

        if (other.Count > 0)
        {
            blocks.Add(new UpcomingBlockViewModel(caption: null, isHighlighted: false, other));
        }

        return blocks;
    }

    // Replace a bound collection's contents with <paramref name="desired"/> only when they differ, so an
    // unchanged list is left untouched and its realized views are reused (UI memoization). Reference-equality
    // is the right comparison: the cards are the same cached VM instances when unchanged.
    private static void SyncTo<T>(
        ObservableCollection<T> target,
        IReadOnlyList<T> desired)
    {
        if (target.Count == desired.Count)
        {
            var same = true;
            for (var i = 0; i < desired.Count; i++)
            {
                if (!ReferenceEquals(target[i], desired[i]))
                {
                    same = false;
                    break;
                }
            }

            if (same)
            {
                return;
            }
        }

        target.Clear();
        foreach (var card in desired)
        {
            target.Add(card);
        }
    }
}

/// <summary>A category as the tab selector presents it: the enum value plus the label the user sees.</summary>
/// <summary>
/// A tab as the selector presents it: the category it shows, the label, and whether it is the Upcoming
/// view (improvement 4). Upcoming is a view over Started, not a fifth category (SPEC R1), so it carries
/// <see cref="IsUpcoming"/> = true and its <see cref="Category"/> is never used to query a category tab.
/// </summary>
public sealed record CategoryTab(ShowCategory Category, string Display, bool IsUpcoming = false);

/// <summary>The sort orders available on the Started list (improvement 2, extended by improvement 12).</summary>
public enum ShowSortOrder
{
    Name,
    AirDate,
    LastAired,
    LastAdded,
}

/// <summary>A sort option as the control presents it: the order plus the label the user sees.</summary>
public sealed record SortOption(ShowSortOrder Order, string Display);
