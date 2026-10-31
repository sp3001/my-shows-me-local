using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Shows.App.Avalonia.Services;

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
        RatingText = episode.RatingAverage?.ToString("0.0", CultureInfo.InvariantCulture);
        RuntimeText = RuntimeFormat.Episode(episode.RuntimeMinutes);
        ImageUrl = episode.ImageUrl;
        FullDescription = string.IsNullOrWhiteSpace(episode.Description)
            ? null
            : episode.Description!.Trim();
        DescriptionText = Truncate(FullDescription);

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

    /// <summary>TVmaze's episode rating, e.g. "8.1"; null when unrated, so the chip hides.</summary>
    public string? RatingText { get; }

    /// <summary>The episode's runtime, e.g. "42 min"; null when the provider has none.</summary>
    public string? RuntimeText { get; }

    /// <summary>The episode still for the info popup; null when TVmaze has none.</summary>
    public string? ImageUrl { get; }

    public bool HasImage => !string.IsNullOrEmpty(ImageUrl);

    /// <summary>The untruncated synopsis, shown in the info popup. Null when TVmaze has no summary.</summary>
    public string? FullDescription { get; }

    /// <summary>The synopsis capped to one trimmed line for the row body.</summary>
    public string DescriptionText { get; }

    public bool HasDescription => DescriptionText.Length > 0;

    /// <summary>
    /// Whether the row gets an info button: only when the popup would actually contain something, so a show
    /// whose episodes have neither a synopsis nor a still keeps a clean row (user request — no icon when
    /// there is nothing to show).
    /// </summary>
    public bool HasInfo => HasDescription || HasImage;

    // Cap the row's synopsis line so a long TVmaze summary cannot grow the row past its two lines; the
    // popup shows the full text. Cut at the last space before the limit so a word is never split.
    private static string Truncate(string? text)
    {
        const int max = 160;
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        if (text.Length <= max)
        {
            return text;
        }

        var cut = text.LastIndexOf(' ', max);
        return (cut > 0 ? text[..cut] : text[..max]).TrimEnd() + "…";
    }
}
