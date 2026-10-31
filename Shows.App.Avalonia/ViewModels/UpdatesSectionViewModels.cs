using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Shows.Application.UseCases;
using Shows.Domain;

namespace Shows.App.Avalonia.ViewModels;

/// <summary>
/// One of the four sections of the Updates tab: a heading over its rows. A section with no rows hides
/// itself entirely, so a quiet month leaves no empty heading behind.
/// </summary>
public sealed class UpdatesSectionViewModel : ObservableObject
{
    public UpdatesSectionViewModel(string caption, List<UpdatesRowViewModel> rows)
    {
        Caption = caption;
        Rows = rows;
    }

    public string Caption { get; }

    public List<UpdatesRowViewModel> Rows { get; }

    public bool HasRows => Rows.Count > 0;
}

/// <summary>
/// One row in an Updates section: <c>(show) (optional status) (optional episode) (date)</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>One row type for four sections.</b> The sections differ only in which date they carry and whether
/// they also show the current status or an episode, so a single shape with visibility gates keeps one view
/// instead of four near-identical ones. The date is always last, which is what makes the four sections
/// scan the same way.
/// </para>
/// <para>
/// Read-only throughout — nothing here is editable, so there is no observable state beyond the navigation
/// command. The show title links to its details page, the same target the tracker cards use.
/// </para>
/// </remarks>
public sealed partial class UpdatesRowViewModel : ObservableObject
{
    /// <summary>A show-shaped row (Ended / Renewed / Updated Lately).</summary>
    public UpdatesRowViewModel(UpdatesFeedRow row, bool showStatus)
    {
        Show = row.Show;
        ShowTitle = TitleRules.Select(row.Show.Title, row.Show.TitleLocalized).Primary;
        StatusText = showStatus ? ShowStatusDisplay.Text(row.Show.Status) : null;
        DateText = FormatDate(row.At);
    }

    /// <summary>A recently-watched row: the same shape plus the episode it names.</summary>
    public UpdatesRowViewModel(RecentlyWatchedRow row)
    {
        Show = row.Show;
        ShowTitle = TitleRules.Select(row.Show.Title, row.Show.TitleLocalized).Primary;
        EpisodeText = FormatEpisode(row.Episode);
        DateText = FormatDate(row.WatchedAt);
    }

    public TrackedShow Show { get; }

    public long TrackedShowId => Show.Id;

    public string ShowTitle { get; }

    /// <summary>The current status label, only for the Renewed section.</summary>
    public string? StatusText { get; }

    public bool HasStatus => !string.IsNullOrEmpty(StatusText);

    /// <summary>The episode label, only for the Recently Watched section.</summary>
    public string? EpisodeText { get; }

    public bool HasEpisode => !string.IsNullOrEmpty(EpisodeText);

    /// <summary>The section's event date, always the right-hand column.</summary>
    public string DateText { get; }

    // yyyy-MM-dd, the format every other date in this app renders with. These are instants rather than
    // calendar dates, but the sections are day-granular views and a time-of-day column would only add
    // noise between two shows that ended the same week.
    private static string FormatDate(DateTimeOffset instant) =>
        instant.ToUniversalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    // "S01E05" plus the episode title when there is one. The number pair is what identifies an episode
    // unambiguously; the title alone does not, and specials have no number to lead with.
    private static string FormatEpisode(Episode episode)
    {
        var number = episode.SeasonNumber.ToString(CultureInfo.InvariantCulture);
        var label = episode.EpisodeNumber is { } ep
            ? $"S{number}E{ep.ToString(CultureInfo.InvariantCulture)}"
            : $"S{number}";

        var title = TitleRules.Select(episode.Title ?? string.Empty, episode.TitleLocalized).Primary;
        return string.IsNullOrEmpty(title) ? label : $"{label} · {title}";
    }

    /// <summary>Navigate to the show's details page (same target as the tracker card).</summary>
    [RelayCommand]
    private Task OpenDetailsAsync() =>
        Shell.Current.GoToAsync($"ShowDetails?id={TrackedShowId.ToString(CultureInfo.InvariantCulture)}");
}
