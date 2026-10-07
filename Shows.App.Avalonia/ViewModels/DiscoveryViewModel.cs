using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Shows.Application;
using Shows.Application.UseCases;

namespace Shows.App.Avalonia.ViewModels;

/// <summary>
/// The Discovery tab (user-issues 19), reworked to run off the cached <c>/schedule/full</c> snapshot
/// rather than the non-existent <c>/newshows</c> / <c>/upcoming</c> endpoints (issue 1). Presents three
/// buckets — New / Airing / Upcoming — with name/genre/network/country/type filters and pagination.
/// </summary>
/// <remarks>
/// <para>
/// Shares <see cref="ShowCardViewModel"/> and the add flow with Search (SPEC F3): the card is the same
/// object, so adding a discovered show is not a forked path. The card's Details button navigates to the
/// same <c>SearchDetails</c> page (user-issues 19's "details link, same as the Search tab").
/// </para>
/// <para>
/// <b>Why the filters are cheap.</b> Unlike Search, Discovery does not hit the network per keystroke —
/// it queries an in-memory snapshot the source refreshes at most daily. So filtering is a local pass; the
/// only network cost is the first snapshot fill, which the source caches. A short debounce on the name
/// box still avoids re-running the (cheap) query on every character.
/// </para>
/// <para>
/// <b>Threading (the "Call from invalid thread" fix).</b> Every await here runs WITHOUT
/// <c>ConfigureAwait(false)</c>, so its continuation resumes on the Avalonia UI dispatcher context that
/// was captured when the command/handler started on the UI thread. The bound <see cref="Results"/>
/// collection and the observable properties are therefore always mutated on the UI thread. The source
/// runs its cheap query inline (no thread-pool hop), so a warm read never leaves the UI thread at all;
/// only a cold snapshot fill yields, and it resumes back on the UI context.
/// </para>
/// <para>
/// <b>The one rule a facet selection must obey.</b> Every value assigned to a <c>Selected*</c> property has
/// to be an instance that is <em>already in</em> that dropdown's bound options collection. Avalonia's
/// <c>SelectingItemsControl.SelectedItem</c> is TwoWay-bound by default and resolves its value by
/// <em>index</em>: an instance the items list does not contain is not found, the index coerces to -1, the
/// selection coerces to <c>null</c>, and that null is written straight back into this ViewModel — the
/// filter silently evaporates while the code that set it still believes it applied. <see cref="FacetOption"/>
/// has no value equality, so a freshly minted option with the right value is a different instance and does
/// not count. Every selection below is therefore taken out of the collection it belongs to, defaults
/// included — see <see cref="EnsureFacetOption"/> and <see cref="DefaultTypeOption"/>.
/// </para>
/// <para>
/// Resolves the use case from a per-operation scope (the VM lives on the Shell's singleton root), so no
/// scoped service is captive — the same pattern as Search.
/// </para>
/// </remarks>
public sealed partial class DiscoveryViewModel : ObservableObject
{
    private const int DebounceMs = 300;

    // The type filter defaults to Scripted (user-issues 19 follow-up) — the user tracks scripted series, so
    // the noisy reality/documentary/animated filler is filtered out until they widen it.
    private const string DefaultShowType = "Scripted";

    private readonly IServiceScopeFactory _scopeFactory;
    private CancellationTokenSource? _debounceCts;

    // True while ReloadAsync is repopulating the facet dropdowns. Re-selecting the current facet fires the
    // OnSelected*Changed handlers, which would otherwise kick off another reload — this flag makes those
    // handlers no-op during a rebuild so a reload triggers exactly one reload, not a cascade.
    private bool _rebuildingFacets;

    // True while a bucket switch clears the name box. The name TextBox is TwoWay-bound, so nulling
    // NameFilter writes "" back through the binding and fires the page's TextChanged → ApplyNameFilter,
    // which would otherwise start a spurious debounced reload on top of the switch's own reload.
    private bool _suppressNameDebounce;

    public DiscoveryViewModel(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory;
        _selectedBucket = BucketOptions[0];

        // Seed the default type into TypeOptions rather than just holding a detached option: the type
        // dropdown cannot select an instance its ItemsSource does not contain (see the class remarks), and
        // before the first reload the list is still empty — so the default has to put itself in.
        _selectedType = DefaultTypeOption();
    }

    [ObservableProperty]
    private BucketOption _selectedBucket;

    [ObservableProperty]
    private string _nameFilter = string.Empty;

    [ObservableProperty]
    private FacetOption? _selectedGenre;

    [ObservableProperty]
    private FacetOption? _selectedNetwork;

    [ObservableProperty]
    private FacetOption? _selectedCountry;

    [ObservableProperty]
    private FacetOption? _selectedType;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPrevious))]
    [NotifyPropertyChangedFor(nameof(HasNext))]
    [NotifyCanExecuteChangedFor(nameof(LoadCommand))]
    [NotifyCanExecuteChangedFor(nameof(NextPageCommand))]
    [NotifyCanExecuteChangedFor(nameof(PreviousPageCommand))]
    private bool _isBusy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PageSummary))]
    [NotifyPropertyChangedFor(nameof(HasPrevious))]
    [NotifyPropertyChangedFor(nameof(HasNext))]
    [NotifyCanExecuteChangedFor(nameof(PreviousPageCommand))]
    private int _page = 1;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PageSummary))]
    [NotifyPropertyChangedFor(nameof(HasNext))]
    [NotifyCanExecuteChangedFor(nameof(NextPageCommand))]
    private int _totalPages;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PageSummary))]
    private int _totalItems;

    [ObservableProperty]
    private string? _statusMessage;

    public ObservableCollection<ShowCardViewModel> Results { get; } = [];

    public ObservableCollection<FacetOption> GenreOptions { get; } = [];

    public ObservableCollection<FacetOption> NetworkOptions { get; } = [];

    public ObservableCollection<FacetOption> CountryOptions { get; } = [];

    public ObservableCollection<FacetOption> TypeOptions { get; } = [];

    public IReadOnlyList<BucketOption> BucketOptions { get; } =
    [
        new(DiscoveryBucket.New, "New Shows"),
        new(DiscoveryBucket.Airing, "Airing Now"),
        new(DiscoveryBucket.Upcoming, "Upcoming"),
    ];

    public bool HasPrevious => Page > 1 && !IsBusy;

    public bool HasNext => Page < TotalPages && !IsBusy;

    public string PageSummary => TotalItems == 0
        ? "No shows."
        : $"Page {Page} of {TotalPages} · {TotalItems} shows";

    private bool CanLoad => !IsBusy;

    // Switching the bucket resets every filter and reloads from page 1 — the snapshot is local, so a switch
    // is a fresh query. Resetting (rather than carrying the old facets over) is the fix for the bug where a
    // filter selected in one bucket had no matching option in the next: RebuildFacets would fall back to
    // "All" for the vanished value while the other facets still zeroed the result, leaving an empty view
    // with a misleading "All" shown. Type is the one exception — it returns to the Scripted default rather
    // than "All", since the user tracks scripted series (see DefaultShowType).
    partial void OnSelectedBucketChanged(BucketOption value)
    {
        ResetFiltersForBucketSwitch();
        _ = ReloadAsync(resetPage: true);
    }

    // Choosing a facet narrows immediately (the dropdowns are the filter UI now, not free text). Guarded so
    // the selection RebuildFacets restores during a reload does not re-enter ReloadAsync.
    partial void OnSelectedGenreChanged(FacetOption? value)
    {
        if (!_rebuildingFacets)
        {
            _ = ReloadAsync(resetPage: true);
        }
    }

    partial void OnSelectedNetworkChanged(FacetOption? value)
    {
        if (!_rebuildingFacets)
        {
            _ = ReloadAsync(resetPage: true);
        }
    }

    partial void OnSelectedCountryChanged(FacetOption? value)
    {
        if (!_rebuildingFacets)
        {
            _ = ReloadAsync(resetPage: true);
        }
    }

    partial void OnSelectedTypeChanged(FacetOption? value)
    {
        if (!_rebuildingFacets)
        {
            _ = ReloadAsync(resetPage: true);
        }
    }

    [RelayCommand(CanExecute = nameof(CanLoad))]
    private Task LoadAsync() => ReloadAsync(resetPage: true);

    [RelayCommand(CanExecute = nameof(CanLoad))]
    private Task NextPageAsync()
    {
        if (Page < TotalPages)
        {
            Page++;
        }

        return ReloadAsync(resetPage: false);
    }

    [RelayCommand(CanExecute = nameof(CanLoad))]
    private Task PreviousPageAsync()
    {
        if (Page > 1)
        {
            Page--;
        }

        return ReloadAsync(resetPage: false);
    }

    /// <summary>Apply the name filter (Enter / the Search button). Resets to page 1.</summary>
    [RelayCommand]
    private Task ApplyFiltersAsync() => ReloadAsync(resetPage: true);

    /// <summary>
    /// Restart the debounce window on a name-box keystroke, then re-query once the user pauses. Public so
    /// the page's text-changed handler drives it. (Named distinctly from the generator's
    /// <c>OnNameFilterChanged</c> partial hook.)
    /// </summary>
    public void ApplyNameFilter(string text)
    {
        NameFilter = text;
        // Ignore the write-back that ResetFiltersForBucketSwitch's NameFilter = "" causes through the
        // TwoWay binding — that clear has its own reload, so a debounce here would double-fire.
        if (_suppressNameDebounce)
        {
            return;
        }

        _ = RunDebouncedAsync();
    }

    /// <summary>
    /// Clear every filter back to its default when the bucket changes: name empty, genre/network/country
    /// unset (they fall back to "All" on the next reload), and type back to the Scripted default. Runs under
    /// <see cref="_rebuildingFacets"/> so the facet setters do not each kick off a reload — the caller
    /// (OnSelectedBucketChanged) issues the single reload after this returns. The name clear runs under
    /// <see cref="_suppressNameDebounce"/> so the binding write-back does not start a competing debounce.
    /// </summary>
    /// <remarks>
    /// The type reset goes through <see cref="DefaultTypeOption"/>, which selects the Scripted instance out
    /// of <see cref="TypeOptions"/>. Assigning a new <c>FacetOption("Scripted", 0)</c> here — which reads
    /// identically and is what this method used to do — does not survive the round trip: the ComboBox cannot
    /// find that instance in its items, coerces its selection to null, and writes the null back, so the
    /// reload that follows queried every type and the dropdown settled on "All" (the bug). The option list
    /// being reset is the last one the previous bucket built, and it carries "Scripted" whenever that bucket
    /// has any scripted show; the reload then refreshes its count in place, keeping the selection alive.
    /// </remarks>
    private void ResetFiltersForBucketSwitch()
    {
        _rebuildingFacets = true;
        _suppressNameDebounce = true;
        try
        {
            NameFilter = string.Empty;
            SelectedGenre = null;
            SelectedNetwork = null;
            SelectedCountry = null;
            SelectedType = DefaultTypeOption();
        }
        finally
        {
            _rebuildingFacets = false;
            _suppressNameDebounce = false;
        }
    }

    /// <summary>
    /// The <see cref="FacetOption"/> instance in <see cref="TypeOptions"/> carrying the default show type,
    /// inserted when the list does not offer it (before the first reload, or in a bucket with no show of
    /// that type, where the option shows a zero count). The type filter is never "unselected": its fallback
    /// is this default, not "All" — the user tracks scripted series (see <see cref="DefaultShowType"/>).
    /// </summary>
    private FacetOption DefaultTypeOption() => EnsureFacetOption(TypeOptions, DefaultShowType);

    /// <summary>Called when the page appears so the first bucket is populated without an explicit tap.</summary>
    public Task InitializeAsync() => Results.Count == 0 ? ReloadAsync(resetPage: true) : Task.CompletedTask;

    private async Task RunDebouncedAsync()
    {
        var previous = _debounceCts;
        var cts = new CancellationTokenSource();
        _debounceCts = cts;
        if (previous is not null)
        {
            await previous.CancelAsync();
        }

        try
        {
            // No ConfigureAwait(false): resumes on the UI context so the reload that follows is on the UI thread.
            await Task.Delay(DebounceMs, cts.Token);
        }
        catch (OperationCanceledException)
        {
            return; // superseded by a newer keystroke
        }

        await ReloadAsync(resetPage: true);
    }

    private async Task ReloadAsync(bool resetPage)
    {
        if (resetPage)
        {
            Page = 1;
        }

        IsBusy = true;
        StatusMessage = null;
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var useCase = scope.ServiceProvider.GetRequiredService<GetDiscoveryPageUseCase>();
            var filter = new DiscoveryFilter(
                Name: NameFilter,
                Genre: SelectedGenre?.Value,
                Network: SelectedNetwork?.Value,
                Country: SelectedCountry?.Value,
                ShowType: SelectedType?.Value);

            // pageSize 0 => the source applies the configured default and reports it back on the page.
            // No ConfigureAwait(false): the continuation must resume on the UI context to mutate Results.
            var result = await useCase
                .ExecuteAsync(SelectedBucket.Bucket, filter, Page, pageSize: 0, CancellationToken.None);

            Page = result.Page;
            TotalPages = result.TotalPages;
            TotalItems = result.TotalItems;

            Results.Clear();
            foreach (var show in result.Items)
            {
                Results.Add(new ShowCardViewModel(show, _scopeFactory));
            }

            // Rebuild the facet dropdowns from the page's counts, preserving the current selection so the
            // user's pick does not vanish when the option list is refreshed. The flag suppresses the
            // OnSelected*Changed reload that re-selecting a facet would otherwise trigger.
            _rebuildingFacets = true;
            try
            {
                RebuildFacets(GenreOptions, result.GenreOptions, SelectedGenre, o => SelectedGenre = o);
                RebuildFacets(NetworkOptions, result.NetworkOptions, SelectedNetwork, o => SelectedNetwork = o);
                RebuildFacets(CountryOptions, result.CountryOptions, SelectedCountry, o => SelectedCountry = o);

                // The type facet falls back to its Scripted default instead of "All" if its value has gone
                // missing from this page's options, so the dropdown can never display a filter the query is
                // not actually applying.
                RebuildFacets(
                    TypeOptions, result.TypeOptions, SelectedType, o => SelectedType = o, DefaultShowType);
            }
            finally
            {
                _rebuildingFacets = false;
            }

            StatusMessage = result.TotalItems == 0 ? "Nothing to show for this filter." : null;
        }
        catch (Exception ex)
        {
            // R14: an unreachable provider throws naming the host, never an empty list.
            StatusMessage = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    // Reconcile the dropdown's items IN PLACE rather than Clear()+re-add. Clearing the bound collection
    // removes the selected instance, which nulls the ComboBox's SelectedItem (TwoWay-written back to the
    // VM) and makes the box fall back to its "Not set" placeholder — the bug where changing one filter
    // blanked that filter's own box while the others kept their value. Updating the existing instances'
    // counts and only adding/removing the differences keeps the selected instance alive, so the selection
    // (and its header) never flickers. The user's selection is re-selected by value: when its value
    // survived, this resolves to the SAME instance (updated in place), so the property setter is a no-op
    // and no reload is triggered. A selection whose value vanished, or an unset facet, falls back to the
    // explicit "All" entry (empty value) — never the "Not set" placeholder, which is reserved for the
    // genuine "shows with no value" filter (user-issues 19 follow-up).
    //
    // A facet with a <paramref name="defaultValue"/> (only the type facet today) never falls back to "All":
    // it falls back to its own default, inserted into the list if this page offers no option for it. "All"
    // is not that facet's neutral state — an unfiltered type list is noise the user does not want — so the
    // box would otherwise read "All" over a query that is still filtering by type.
    private static void RebuildFacets(
        ObservableCollection<FacetOption> target,
        IReadOnlyList<DiscoveryFacetOption> options,
        FacetOption? current,
        Action<FacetOption?> select,
        string? defaultValue = null)
    {
        var existing = new Dictionary<string, FacetOption>(StringComparer.OrdinalIgnoreCase);
        foreach (var o in target)
        {
            existing[o.Value] = o;
        }

        // A new option goes in at the position the source reported it at, not on the end. The type list is
        // pre-seeded with its default by the constructor, so appending would strand "All" behind "Scripted"
        // permanently — the order of the dropdown is the user's mental model of the facet and must not depend
        // on what happened to be selected first. An option that already exists keeps its slot, so a value that
        // survives a reload never jumps position under the user's cursor; the one exception is the default
        // re-inserted for a bucket that offers no such option (no source position to take), which lands last.
        // Inserting above a live selection is safe — Avalonia shifts the selection along with the item
        // (measured: docs/LOG.md, 2026-10-07).
        for (var i = 0; i < options.Count; i++)
        {
            var o = options[i];
            if (existing.TryGetValue(o.Value, out var match))
            {
                match.Count = o.Count; // update in place; the selected instance keeps its identity
            }
            else
            {
                target.Insert(Math.Min(i, target.Count), new FacetOption(o.Value, o.Count));
            }
        }

        var desired = new HashSet<string>(options.Select(o => o.Value), StringComparer.OrdinalIgnoreCase);
        for (var i = target.Count - 1; i >= 0; i--)
        {
            if (!desired.Contains(target[i].Value))
            {
                target.RemoveAt(i);
            }
        }

        var keep = current is null
            ? null
            : target.FirstOrDefault(o => string.Equals(o.Value, current.Value, StringComparison.OrdinalIgnoreCase));

        // Both fallback branches resolve to an instance inside `target` — the selection must always be one
        // of the items the dropdown is bound to, or it is coerced away and written back as null.
        select(keep ?? (defaultValue is not null
            ? EnsureFacetOption(target, defaultValue)
            : target.FirstOrDefault(o => o.Value.Length == 0)));
    }

    // Return the option in `target` carrying `value`, appending it when absent (count 0 — the list is the
    // dropdown's ItemsSource, so anything the VM wants to select has to live in it). Value lookup is
    // case-insensitive because the option values come from TVmaze text, and a FacetOption's identity is its
    // reference: a matching-by-value search is what keeps a selection stable across rebuilds.
    private static FacetOption EnsureFacetOption(ObservableCollection<FacetOption> target, string value)
    {
        var existing = target.FirstOrDefault(
            o => string.Equals(o.Value, value, StringComparison.OrdinalIgnoreCase));

        if (existing is not null)
        {
            return existing;
        }

        var option = new FacetOption(value, 0);
        target.Add(option);
        return option;
    }
}

/// <summary>A bucket as the switcher presents it: the enum value plus the label the user sees.</summary>
public sealed record BucketOption(DiscoveryBucket Bucket, string Display);

/// <summary>
/// One facet dropdown entry: the underlying value and the count shown beside it. The label is built here so
/// the dropdown reads "Drama (42)" / "All (120)" / "Not set (4)" without per-view string logic. An empty
/// <see cref="Value"/> is the "All" entry; <see cref="DiscoveryFilter.NotSet"/> is the "Not set" entry
/// (shows whose value for this facet is unfilled). Observable on <see cref="Count"/> so a rebuild can
/// refresh a facet's count in place — keeping the same instance (and thus the ComboBox selection) alive
/// while its number changes.
/// </summary>
public sealed class FacetOption(string value, int count) : ObservableObject
{
    public string Value { get; } = value;

    private int _count = count;

    public int Count
    {
        get => _count;
        set
        {
            if (_count != value)
            {
                _count = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(Label));
            }
        }
    }

    public string Label => Value switch
    {
        "" => $"All ({Count})",
        DiscoveryFilter.NotSet => $"Not set ({Count})",
        _ => $"{Value} ({Count})",
    };

    public override string ToString() => Label;
}
