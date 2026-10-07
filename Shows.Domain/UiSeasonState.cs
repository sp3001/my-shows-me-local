namespace Shows.Domain;

/// <summary>
/// Whether a season's episode list is expanded for one show.
/// </summary>
/// <remarks>
/// Persisted per show and per season rather than as a global setting, and it survives the UI rewrite
/// (ADR-012). User-owned (SPEC P4) and untouched by untracking, for the same reason as
/// <see cref="EpisodeWatch"/>: all seasons start collapsed (SPEC R9) and the state is restored on
/// next launch.
/// </remarks>
public sealed class UiSeasonState
{
    public long Id { get; set; }

    public long ShowId { get; set; }
    public long SeasonId { get; set; }

    public bool IsExpanded { get; set; }
}
