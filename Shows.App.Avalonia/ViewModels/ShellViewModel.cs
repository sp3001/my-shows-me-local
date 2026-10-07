using System.Collections.Generic;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Shows.App.Avalonia.Services;

namespace Shows.App.Avalonia.ViewModels;

/// <summary>
/// The Avalonia replacement for MAUI's <c>Shell</c>: it owns the four top-level tabs (My Shows,
/// Search, Discovery, Manage) and a navigation stack for the details pages reached by
/// <c>Shell.Current.GoToAsync</c>. The window binds a tab strip to <see cref="Tabs"/> and a
/// <c>ContentControl</c> to <see cref="CurrentPage"/>; the <see cref="ViewLocator"/> resolves whichever
/// view model is current to its view.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a stack rather than Shell routing.</b> Avalonia imposes no navigation framework (the migration
/// guide's "Shell → no equivalent"). The ViewModels still call the MAUI route strings via the
/// <see cref="Shell"/> shim, which forwards here; this class turns those into push/pop over
/// <see cref="CurrentPage"/>. Pushing a details page leaves the selected tab intact, so a pop returns to
/// the tab the user came from.
/// </para>
/// <para>
/// The tab view models are constructed once (held as properties) so their loaded lists survive a tab
/// switch — matching the MAUI Shell, where each tab's page persists. A details page is transient: it is
/// created per navigation by <see cref="NavigationService"/> and discarded on pop.
/// </para>
/// </remarks>
public sealed partial class ShellViewModel : ObservableObject
{
    // The details-page stack beneath the current page. Empty while a tab is showing.
    private readonly Stack<object> _stack = new();

    public ShellViewModel(
        TrackingViewModel tracking,
        SearchViewModel search,
        DiscoveryViewModel discovery,
        ManageViewModel manage,
        NavigationService navigation)
    {
        Tracking = tracking;
        Search = search;
        Discovery = discovery;
        Manage = manage;

        Tabs = new[]
        {
            new ShellTab(tracking, "My Shows"),
            new ShellTab(search, "Search"),
            new ShellTab(discovery, "Discovery"),
            new ShellTab(manage, "Manage"),
        };

        // Take over Shell.Current so the ViewModels' GoToAsync calls route here.
        navigation.Attach(this);

        SelectedTab = Tabs[0];
    }

    public TrackingViewModel Tracking { get; }

    public SearchViewModel Search { get; }

    public DiscoveryViewModel Discovery { get; }

    public ManageViewModel Manage { get; }

    /// <summary>The four tabs, in display order. The tab strip binds to this.</summary>
    public IReadOnlyList<ShellTab> Tabs { get; }

    [ObservableProperty]
    private ShellTab? _selectedTab;

    /// <summary>The view model the window's ContentControl renders — a tab or a pushed details page.</summary>
    [ObservableProperty]
    private object? _currentPage;

    /// <summary>True when a details page is pushed over a tab, so the window shows a Back button.</summary>
    public bool CanGoBack => _stack.Count > 0;

    partial void OnSelectedTabChanged(ShellTab? value)
    {
        // Switching tabs resets the navigation stack — a tab is always the root.
        _stack.Clear();
        CurrentPage = value?.ViewModel;
        OnPropertyChanged(nameof(CanGoBack));
        _ = InitializeTabAsync(value?.ViewModel);
    }

    /// <summary>Tab-strip tap: select the tab (which clears any details page and shows the tab).</summary>
    [RelayCommand]
    private void SelectTab(ShellTab tab) => SelectedTab = tab;

    /// <summary>
    /// Push a details page over the current content. Called by <see cref="NavigationService"/> for a
    /// <c>ShowDetails</c> / <c>SearchDetails</c> route. The selected tab is left untouched so a pop
    /// returns to it.
    /// </summary>
    public void Push(object page)
    {
        if (CurrentPage is { } current)
        {
            _stack.Push(current);
        }

        CurrentPage = page;
        OnPropertyChanged(nameof(CanGoBack));
    }

    /// <summary>Pop back one page (the <c>GoToAsync("..")</c> route). A no-op when already on a tab.</summary>
    public void Pop()
    {
        if (_stack.Count > 0)
        {
            CurrentPage = _stack.Pop();
            OnPropertyChanged(nameof(CanGoBack));
            // Returning to a tab is MAUI's OnAppearing: reload it so changes made on the details page
            // (e.g. unchecking an episode) are reflected. Discovery self-guards, so this is a no-op there.
            _ = InitializeTabAsync(CurrentPage);
        }
    }

    /// <summary>Back-button command: pop the details stack (the <c>GoToAsync("..")</c> equivalent).</summary>
    [RelayCommand]
    private void GoBack() => Pop();

    // Load for a tab each time it becomes current (the MAUI OnAppearing equivalent). Tracking re-reads its
    // list on every appearance — so a watch-toggle made on a details page shows up when the user pops back —
    // while Discovery self-guards (it only loads the first time) and Search needs nothing here.
    private Task InitializeTabAsync(object? tab) => tab switch
    {
        TrackingViewModel t => t.InitializeAsync(),
        DiscoveryViewModel d => d.InitializeAsync(),
        _ => Task.CompletedTask,
    };
}

/// <summary>A top-level tab as the strip presents it: the view model it shows plus the label the user sees.</summary>
public sealed record ShellTab(object ViewModel, string Title);
