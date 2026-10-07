using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Shows.App.Avalonia.Services;
using Shows.Application.UseCases;

namespace Shows.App.Avalonia.ViewModels;

/// <summary>
/// The lightweight details view for an <b>untracked</b> search result (user-issues 3 / improvement 10).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a separate page from <c>ShowDetailsPage</c>.</b> That page loads a <c>tracked_show</c> by
/// surrogate id (SPEC F5); a search hit has none — it is not persisted until it is added (SPEC P2/R8a).
/// So this page reads the catalog record by TVmaze id via <see cref="GetCatalogShowUseCase"/> and renders
/// it, offering the same Start / Later add actions as the search card. Once the user adds the show, the
/// card's already-tracked marker flips and the buttons disable in place.
/// </para>
/// <para>
/// The content is a <see cref="ShowCardViewModel"/> built from the fetched <c>CatalogShow</c>, so the
/// title rule (R3), the meta line, and the add flow are the exact same objects the search list uses —
/// no second copy of the add path. Like the search VM, this holds only the singleton
/// <see cref="IServiceScopeFactory"/> and opens a per-operation scope (no captive scoped service).
/// </para>
/// </remarks>
public sealed partial class SearchDetailsViewModel : ObservableObject
{
    private readonly IServiceScopeFactory _scopeFactory;

    public SearchDetailsViewModel(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory;
    }

    /// <summary>The show to render, with its add commands. Null until loaded (or when not found).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    private ShowCardViewModel? _card;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    private bool _isBusy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    private string? _statusMessage;

    /// <summary>True when there is nothing to show — still loading, or a friendly error/not-found.</summary>
    public bool IsEmpty => Card is null;

    /// <summary>True when the show has episodes to list below the card.</summary>
    public bool HasSeasons => Seasons.Count > 0;

    /// <summary>The season groups rendered under the card. Empty until loaded.</summary>
    public ObservableCollection<CatalogSeasonViewModel> Seasons { get; } = [];

    /// <summary>
    /// The TVmaze link for the loaded show, built from its id at load (SPEC P5, improvement 20). Empty
    /// until loaded (or when the format is blank), so the button hides.
    /// </summary>
    public string TvmazeUrl { get; private set; } = string.Empty;

    /// <summary>The IMDb link for the loaded show; empty when it has no IMDb id or the format is blank.</summary>
    public string ImdbUrl { get; private set; } = string.Empty;

    public bool HasImdbLink => !string.IsNullOrEmpty(ImdbUrl);

    /// <summary>
    /// The show's official-site URL. Unlike the TVmaze/IMDb links this is NOT derived from an id — it is
    /// authored catalog data (TVmaze <c>officialSite</c>) carried on the read model. Empty when the show
    /// has none, so the button hides.
    /// </summary>
    public string OfficialSiteUrl { get; private set; } = string.Empty;

    public bool HasOfficialSiteLink => !string.IsNullOrEmpty(OfficialSiteUrl);

    /// <summary>
    /// Load the catalog show by TVmaze id. R14: an unreachable provider throws naming the host; a stale
    /// id returns null. Either way the page shows the message rather than crashing.
    /// </summary>
    public async Task LoadAsync(int tvmazeId)
    {
        IsBusy = true;
        StatusMessage = null;
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var get = scope.ServiceProvider.GetRequiredService<GetCatalogDetailsUseCase>();
            // No ConfigureAwait(false): the continuation mutates the bound Seasons collection and must
            // resume on the Avalonia UI context. The show card may come from the cache, but the episode
            // list is always fetched live (the full guide), for both Discovery and Search hits.
            var details = await get.ExecuteAsync(tvmazeId, CancellationToken.None);

            if (details is null)
            {
                StatusMessage = "This show is no longer available in the catalog.";
                Card = null;
                Seasons.Clear();
                SetLinks(string.Empty, string.Empty, string.Empty);
                return;
            }

            // The card is rendered inside this very details page, so its title link would navigate to the
            // page the user is already on — suppress it (ShowCardViewModel.ShowTitleLink).
            Card = new ShowCardViewModel(details.Show, _scopeFactory, showTitleLink: false, largePoster: true);

            // SPEC P5 / improvement 20: the TVmaze/IMDb links are a function of the ids, built here — the
            // show's TVmaze id is always present, the IMDb id only when the catalog carried one. The
            // official site is authored data carried on the read model (not derived from an id).
            SetLinks(
                ShowLinks.Tvmaze(details.Show.TvmazeId),
                ShowLinks.Imdb(details.Show.ImdbId),
                details.Show.OfficialSite ?? string.Empty);

            Seasons.Clear();
            foreach (var season in details.Seasons)
            {
                Seasons.Add(new CatalogSeasonViewModel(season));
            }
        }
        catch (Exception ex)
        {
            // R14: surface the host-naming message; never render an empty page as "not found".
            StatusMessage = ex.Message;
            Card = null;
            Seasons.Clear();
            SetLinks(string.Empty, string.Empty, string.Empty);
        }
        finally
        {
            IsBusy = false;
            OnPropertyChanged(nameof(HasSeasons));
        }
    }

    private void SetLinks(string tvmazeUrl, string imdbUrl, string officialSiteUrl)
    {
        TvmazeUrl = tvmazeUrl;
        ImdbUrl = imdbUrl;
        OfficialSiteUrl = officialSiteUrl;
        OnPropertyChanged(nameof(TvmazeUrl));
        OnPropertyChanged(nameof(ImdbUrl));
        OnPropertyChanged(nameof(HasImdbLink));
        OnPropertyChanged(nameof(OfficialSiteUrl));
        OnPropertyChanged(nameof(HasOfficialSiteLink));
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
}
