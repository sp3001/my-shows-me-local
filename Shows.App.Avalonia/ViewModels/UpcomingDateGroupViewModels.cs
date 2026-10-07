using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Shows.Application.UseCases;
using Shows.Domain;

namespace Shows.App.Avalonia.ViewModels;

/// <summary>
/// The Upcoming tab regrouped by <b>date</b> (improvement 4 follow-up). The tab used to lead with the show
/// and nest its seasons; the user asked instead for two date blocks — a highlighted <b>This Week</b>
/// (today through today + 7 days, hidden when empty) and an unlabelled <b>Other</b> — each holding day
/// groups, and each day a header over its episode rows.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the grouping lives here.</b> The use case returns a flat, soonest-first episode list; slicing it
/// into blocks and days is presentation, so it happens in the UI over that ordered list in one pass. An
/// episode with an unknown air date has no date to place, so it lands in <b>Other</b> under a
/// "Unknown date" day (the use case already sorts those last).
/// </para>
/// </remarks>
public sealed class UpcomingBlockViewModel : ObservableObject
{
    public UpcomingBlockViewModel(string? caption, bool isHighlighted, List<UpcomingDayViewModel> days)
    {
        Caption = caption;
        IsHighlighted = isHighlighted;
        Days = days;
    }

    /// <summary>The block heading ("This Week"), or null for the unlabelled "Other" block.</summary>
    public string? Caption { get; }

    public bool HasCaption => !string.IsNullOrEmpty(Caption);

    /// <summary>True for the "This Week" block, which the view tints with a soft background.</summary>
    public bool IsHighlighted { get; }

    /// <summary>The day groups inside this block, soonest date first.</summary>
    public List<UpcomingDayViewModel> Days { get; }
}

/// <summary>
/// One calendar day inside an Upcoming block: a date header over the episodes airing that day. A day with
/// an unknown air date renders the "Unknown date" header instead.
/// </summary>
public sealed class UpcomingDayViewModel : ObservableObject
{
    public UpcomingDayViewModel(DateOnly? date)
    {
        Date = date;
        if (date is { } d)
        {
            // "10 October 2016" on the main line, the weekday ("wednesday") beneath in a smaller font.
            DateText = d.ToString("d MMMM yyyy", CultureInfo.InvariantCulture);
            // CA1308 flags lowercase normalisation as unsafe for case-insensitive comparison; here the
            // lowercased weekday is display-only text (never compared or hashed), so it is intentional.
#pragma warning disable CA1308
            WeekdayText = d.ToString("dddd", CultureInfo.InvariantCulture).ToLowerInvariant();
#pragma warning restore CA1308
        }
        else
        {
            DateText = "Unknown date";
            WeekdayText = string.Empty;
        }
    }

    public DateOnly? Date { get; }

    /// <summary>The day header's main line — "10 October 2016", or "Unknown date" for undated episodes.</summary>
    public string DateText { get; }

    /// <summary>The weekday beneath the date ("wednesday"), rendered smaller. Empty for an undated day.</summary>
    public string WeekdayText { get; }

    public bool HasWeekday => !string.IsNullOrEmpty(WeekdayText);

    /// <summary>The episodes airing on this day, ordered by show then season/number.</summary>
    public List<UpcomingEpisodeRowViewModel> Rows { get; } = [];
}

/// <summary>
/// One episode row in the Upcoming tab's date grouping: <c>(Show) (Season) (Episode or "Special") (Name)</c>.
/// Read-only — an episode that has not aired cannot be marked watched. The show title links to its details.
/// </summary>
public sealed partial class UpcomingEpisodeRowViewModel : ObservableObject
{
    public UpcomingEpisodeRowViewModel(UpcomingEpisode upcoming)
    {
        Show = upcoming.Show;
        var episode = upcoming.Episode;

        var title = TitleRules.Select(upcoming.Show.Title, upcoming.Show.TitleLocalized);
        ShowTitle = title.Primary;

        SeasonText = $"S{episode.SeasonNumber.ToString(CultureInfo.InvariantCulture)}";

        // The request: the episode number, or the word "Special" for a significant special. A null number
        // (IMDb-legacy) on a non-special falls back to the marker-less blank rather than a fake number.
        EpisodeText = episode.EpisodeType == EpisodeType.Special
            ? "Special"
            : episode.EpisodeNumber is { } n
                ? $"E{n.ToString(CultureInfo.InvariantCulture)}"
                : string.Empty;

        var epTitle = TitleRules.Select(episode.Title ?? string.Empty, episode.TitleLocalized);
        EpisodeName = string.IsNullOrEmpty(epTitle.Primary) ? null : epTitle.Primary;
    }

    public TrackedShow Show { get; }

    public long TrackedShowId => Show.Id;

    public string ShowTitle { get; }

    public string SeasonText { get; }

    public string EpisodeText { get; }

    public string? EpisodeName { get; }

    public bool HasEpisodeName => !string.IsNullOrEmpty(EpisodeName);

    /// <summary>Navigate to the show's details page (same target as the tracker card).</summary>
    [RelayCommand]
    private Task OpenDetailsAsync() =>
        Shell.Current.GoToAsync($"ShowDetails?id={TrackedShowId.ToString(CultureInfo.InvariantCulture)}");
}
