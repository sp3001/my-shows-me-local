using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Shows.App.Avalonia.ViewModels;

/// <summary>
/// One season in the <b>untracked</b> details list (user-issues 19 follow-up). Read-only: an untracked
/// show has no watch state, so unlike <see cref="SeasonGroupViewModel"/> there is no toggle, no lazy
/// load, and no persistence — it is a flat header plus its episode rows, built once from the
/// <see cref="Shows.Application.CatalogSeason"/> the details use case returned.
/// </summary>
public sealed class CatalogSeasonViewModel
{
    public CatalogSeasonViewModel(Shows.Application.CatalogSeason season)
    {
        Header = season.SeasonNumber == 0
            ? "Specials"
            : $"Season {season.SeasonNumber.ToString(CultureInfo.InvariantCulture)}";
        Episodes = season.Episodes.Select(e => new CatalogEpisodeViewModel(e)).ToList();
    }

    public string Header { get; }

    public IReadOnlyList<CatalogEpisodeViewModel> Episodes { get; }
}

/// <summary>
/// One episode row in the untracked details list. Read-only (no watch toggle). A special shows a star
/// glyph in place of an S/E number — the same marker the tracked rows use (improvement: specials should
/// be recognisable, not a bare "S" with no number).
/// </summary>
public sealed class CatalogEpisodeViewModel
{
    public CatalogEpisodeViewModel(Shows.Application.CatalogEpisode episode)
    {
        IsSpecial = episode.IsSpecial;
        Title = string.IsNullOrWhiteSpace(episode.Title) ? "(untitled)" : episode.Title!;
        AirDateText = episode.AirDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        NumberText = IsSpecial
            ? EpisodeRowViewModel.SpecialGlyph
            : episode.Number is { } n
                ? $"S{episode.SeasonNumber.ToString(CultureInfo.InvariantCulture)}"
                    + $"E{n.ToString(CultureInfo.InvariantCulture)}"
                : $"S{episode.SeasonNumber.ToString(CultureInfo.InvariantCulture)}";
    }

    public string NumberText { get; }

    public string Title { get; }

    public string? AirDateText { get; }

    public bool IsSpecial { get; }
}
