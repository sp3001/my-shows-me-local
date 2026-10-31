using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Shows.Application;
using Shows.Application.UseCases;
using Shows.Domain;

namespace Shows.App.Avalonia.ViewModels;

/// <summary>
/// One catalog hit rendered as a card — shared by Search (4.1) and Discovery (4.2) so the add flow and
/// the R3 title rule live in exactly one place (SPEC F3: "sharing <c>ShowCardViewModel</c>").
/// </summary>
/// <remarks>
/// <para>
/// Wraps a <see cref="CatalogShow"/> read model; it is <b>not</b> a domain entity and is never saved
/// (SPEC P2/R8a). The only writes it can trigger are the explicit <see cref="AddToStartedCommand"/> and
/// <see cref="AddToLaterCommand"/>, which run <see cref="AddShowToTrackingUseCase"/> — the sole path that
/// creates <c>tracked_show</c> + <c>user_show</c>.
/// </para>
/// <para>
/// <b>Why a scope factory rather than the use case directly.</b> The card lives on the Shell's singleton
/// root (it is bound into a page that outlives any single request), but the use case and its repositories
/// are scoped to a DbContext. Capturing a scoped service in a long-lived object is the captive-dependency
/// bug, so the card holds only the (singleton) <see cref="IServiceScopeFactory"/> and opens a fresh scope
/// per add. This is the one place a ViewModel reaches the container, and it reaches only the factory.
/// </para>
/// </remarks>
public sealed partial class ShowCardViewModel : ObservableObject
{
    private readonly IServiceScopeFactory _scopeFactory;

    public ShowCardViewModel(CatalogShow show, IServiceScopeFactory scopeFactory, bool showTitleLink = true, bool largePoster = false)
    {
        Show = show;
        _scopeFactory = scopeFactory;
        ShowTitleLink = showTitleLink;
        LargePoster = largePoster;

        // SPEC R3: localized title primary, original beneath. TitleRules is the single owner of that rule.
        var title = TitleRules.Select(show.Title, show.TitleLocalized);
        PrimaryTitle = title.Primary;
        SecondaryTitle = title.Secondary;

        // The already-tracked marker is computed by the use case against user_show (SPEC F2); the card
        // just reflects it so the add buttons are disabled for a show the user is already tracking.
        _isAlreadyTracked = show.IsAlreadyTracked;
    }

    /// <summary>
    /// Whether the card's title is a clickable link to the details page. True on the Search and Discovery
    /// lists, where tapping the title opens the details page (tracked shows go to their tracked-details
    /// page, untracked ones to the catalog page). False when the card is rendered <i>inside</i> that same
    /// details page (<see cref="SearchDetailsViewModel"/> reuses this card), where the link would navigate
    /// to the page the user is already on.
    /// </summary>
    public bool ShowTitleLink { get; }

    /// <summary>
    /// Whether to render the poster at the larger details size (150×225) rather than the compact list
    /// size (70×100). True only when the card is rendered inside <see cref="SearchDetailsViewModel"/>,
    /// matching the tracked-details page; the Search/Discovery lists leave it false.
    /// </summary>
    public bool LargePoster { get; }

    /// <summary>
    /// <summary>The underlying read model, kept so a caller can read fields not surfaced as properties.</summary>
    public CatalogShow Show { get; }

    public int TvmazeId => Show.TvmazeId;

    public string PrimaryTitle { get; }

    public string? SecondaryTitle { get; }

    public bool HasSecondaryTitle => !string.IsNullOrEmpty(SecondaryTitle);

    public string? YearText => Show.Year?.ToString(CultureInfo.InvariantCulture);

    public string? StatusText => Show.StatusRaw;

    public string? RatingText =>
        Show.RatingAverage is null ? null : Show.RatingAverage.Value.ToString("0.0", CultureInfo.InvariantCulture);

    /// <summary>
    /// TVmaze's rating as a grey chip on the title line (user request: it sat at the end of the meta line,
    /// where a long genre list pushed it out of sight). Null hides the chip.
    /// </summary>
    public string? RatingChipText => RatingText is { Length: > 0 } rating ? "\u2605 " + rating : null;

    /// <summary>
    /// The country as a grey chip on the title line. The read model already carries the finished label
    /// (<see cref="CountryRules.Display"/> applied by the provider), so this page, the tracked card and the
    /// Discovery facet can never disagree about what country a show reads.
    /// </summary>
    public string? CountryText =>
        string.IsNullOrWhiteSpace(Show.CountryName) ? null : Show.CountryName!;

    /// <summary>Whether the grey chip group after the dot has anything to show.</summary>
    public bool HasTitleMetaChips => CountryText is not null || RatingChipText is not null;

    /// <summary>
    /// The dot between the status chip and the catalog chips (country, TVmaze rating). Shown only when there
    /// is something on both sides, so the line never starts or ends with a separator.
    /// </summary>
    public bool ShowChipSeparator =>
        StatusText is { Length: > 0 } && HasTitleMetaChips;

    /// <summary>
    /// The card's meta line — just the year. Status and TVmaze's rating moved to chips on the title line
    /// (user request), and the genres have their own line, so this line is what is left of the old
    /// year · status · ★rating run. Assembled here rather than in XAML so an absent part leaves no dangling
    /// separator (a Span has no IsVisible).
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

            return string.Join("  ·  ", parts);
        }
    }

    public bool HasMetaText => MetaText.Length > 0;

    /// <summary>
    /// The genre line, led by the show's type (Scripted / Animated / Documentary / …) when known — the
    /// type is TVmaze's own human-readable <c>type</c> string, so it reads as just another facet ahead of
    /// the genres. Absent type or genres simply drop out; both absent leaves the line empty.
    /// </summary>
    public string GenresText
    {
        get
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(Show.ShowType))
            {
                parts.Add(Show.ShowType!);
            }

            parts.AddRange(Show.Genres);
            return string.Join("  ·  ", parts);
        }
    }

    /// <summary>
    /// The broadcaster · premiere-date line (user-issues 19 follow-up: the card had room and the facets were
    /// already on the read model; the full premiere date is appended after the network). The country moved
    /// to a chip on the title line. Assembled here so an absent part leaves no dangling separator.
    /// </summary>
    public string NetworkText
    {
        get
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(Show.NetworkName))
            {
                parts.Add(Show.NetworkName!);
            }

            if (Show.PremiereDate is { } premiere)
            {
                parts.Add(premiere.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            }

            return string.Join("  ·  ", parts);
        }
    }

    public bool HasNetworkText => NetworkText.Length > 0;

    /// <summary>
    /// The episode line — marker · name · date. Filled only for Discovery, which has the schedule
    /// snapshot; Search returns no episode data so this stays empty there and the line is hidden. The
    /// leading label depends on <see cref="CatalogNextEpisode.IsRecent"/>: a future episode reads
    /// <c>Next Episode: S02E05 · The Reckoning · 2026-11-04</c>; when the show has nothing scheduled
    /// ahead, the source instead returns its latest known past episode flagged recent, and the line reads
    /// <c>Recent Episode: …</c> so the card still says something useful about a show with no future date.
    /// </summary>
    public string NextEpisodeText
    {
        get
        {
            if (Show.NextEpisode is not { } next)
            {
                return string.Empty;
            }

            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(next.SeasonEpisode))
            {
                parts.Add(next.SeasonEpisode!);
            }

            if (!string.IsNullOrWhiteSpace(next.Title))
            {
                parts.Add(next.Title!);
            }

            if (next.AirDate is { } date)
            {
                parts.Add(date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            }

            if (parts.Count == 0)
            {
                return string.Empty;
            }

            var label = next.IsRecent ? "Recent Episode: " : "Next Episode: ";
            return label + string.Join("  ·  ", parts);
        }
    }

    public bool HasNextEpisodeText => NextEpisodeText.Length > 0;

    public string? Overview => Show.Overview;

    /// <summary>
    /// The synopsis for the card body (user-issues 19 follow-up: fill the card's empty space). The overview
    /// is already plain text (the mapper strips the markup), so this only caps it — the first 1000 chars,
    /// trimmed at a word boundary with an ellipsis when cut. Null/empty when the show has no synopsis.
    /// </summary>
    public string? DescriptionText
    {
        get
        {
            const int max = 1000;
            var text = Show.Overview;
            if (string.IsNullOrWhiteSpace(text))
            {
                return null;
            }

            text = text.Trim();
            if (text.Length <= max)
            {
                return text;
            }

            // Cut at the last whitespace before the limit so a word is not split mid-way.
            var cut = text.LastIndexOf(' ', max);
            return (cut > 0 ? text[..cut] : text[..max]).TrimEnd() + "…";
        }
    }

    public bool HasDescription => !string.IsNullOrEmpty(DescriptionText);

    /// <summary>Medium poster when present (lighter to load), else the original.</summary>
    public string? PosterUrl => Show.PosterMediumUrl ?? Show.PosterUrl;

    public bool HasPoster => !string.IsNullOrEmpty(PosterUrl);

    [ObservableProperty]
    private bool _isAlreadyTracked;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanAdd))]
    [NotifyCanExecuteChangedFor(nameof(AddToStartedCommand))]
    [NotifyCanExecuteChangedFor(nameof(AddToLaterCommand))]
    private bool _isBusy;

    [ObservableProperty]
    private string? _statusMessage;

    public bool CanAdd => !IsBusy && !IsAlreadyTracked;

    /// <summary>
    /// Add straight into <c>Started</c> (SPEC R8). The two category buttons are always visible on the
    /// card rather than hidden behind a drawer: a <c>CollectionView</c> cell measures once and does not
    /// re-measure when a child's <c>IsVisible</c> flips, which is what made the old "Details" drawer —
    /// and the Add button inside it — unreachable (user-issues 3).
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanAdd))]
    private Task AddToStartedAsync() => AddAsync(ShowCategory.Started, "Started");

    /// <summary>Add straight into <c>Later</c> (SPEC R8). See <see cref="AddToStartedCommand"/>.</summary>
    [RelayCommand(CanExecute = nameof(CanAdd))]
    private Task AddToLaterAsync() => AddAsync(ShowCategory.Later, "Later");

    /// <summary>
    /// Open the details page for this show. An already-tracked show goes to its tracked-details page
    /// (<c>ShowDetails?id=…</c>, keyed by the <c>tracked_show</c> id the use case resolved), so the user
    /// lands on the page with their seasons, watch toggles, and progress. An untracked show has no
    /// tracked row yet, so it opens the lightweight catalog-details page (<c>SearchDetails?tvmazeId=…</c>),
    /// which reads the catalog record instead. Navigation is a UI concern, so the card drives it directly.
    /// </summary>
    [RelayCommand]
    private Task OpenDetailsAsync() =>
        Show.TrackedShowId is { } trackedId
            ? Shell.Current.GoToAsync($"ShowDetails?id={trackedId.ToString(CultureInfo.InvariantCulture)}")
            : Shell.Current.GoToAsync($"SearchDetails?tvmazeId={TvmazeId.ToString(CultureInfo.InvariantCulture)}");

    /// <summary>
    /// Add to the given category (SPEC R8). Runs the add use case in its own scope; a transport failure
    /// (R14) or any error surfaces as a status message rather than an unhandled exception — the card must
    /// not crash the list it lives in.
    /// </summary>
    private async Task AddAsync(ShowCategory category, string display)
    {
        IsBusy = true;
        StatusMessage = null;
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var add = scope.ServiceProvider.GetRequiredService<AddShowToTrackingUseCase>();
            // No ConfigureAwait(false): the command started on the Avalonia UI thread, so the continuation
            // must resume there. The lines after the await write bound properties (IsAlreadyTracked /
            // StatusMessage / IsBusy); raising PropertyChanged for a bound property off the UI thread throws
            // "Call from invalid thread", and because the catch/finally write bound properties too, that
            // exception is unhandled on a background thread and terminates the process before the log
            // flushes — the DB write had already committed, so the show appeared only after a restart.
            await add.ExecuteAsync(TvmazeId, category, CancellationToken.None);

            // The add succeeded: reflect it on the card so the buttons disable without a re-search.
            IsAlreadyTracked = true;
            StatusMessage = $"Added to {display}.";
        }
        catch (Exception ex)
        {
            // R14: an unreachable provider throws naming the host. Show it; do not swallow into silence.
            StatusMessage = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }
}
