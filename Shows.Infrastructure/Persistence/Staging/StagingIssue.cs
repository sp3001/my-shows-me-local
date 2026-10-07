namespace Shows.Infrastructure.Persistence.Staging;

/// <summary>
/// A row of <c>v_staging_issues</c> (SCHEMA.md §6) — the promote gate. Keyless: it is a database view,
/// created by hand-written SQL in migration 0003, not by EF. <c>--promote</c> refuses to run while any
/// row has <see cref="Severity"/> = "blocking".
/// </summary>
public sealed class StagingIssue
{
    public int MyshowsId { get; set; }
    public string? TitleRu { get; set; }

    /// <summary>The CASE label: unresolved_tvmaze_id | unresolved_imdb_id | watched_exceeds_total | zero_total_episodes | imdb_count_mismatch | tvmaze_count_mismatch.</summary>
    public string? Issue { get; set; }

    /// <summary>'blocking' or 'warning'.</summary>
    public string? Severity { get; set; }
}
