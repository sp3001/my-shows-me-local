namespace Shows.Domain;

/// <summary>
/// The one place that decides whether a <see cref="ShowStatus"/> means "no more episodes are
/// expected". Owned by SPEC R12's per-value table.
/// </summary>
/// <remarks>
/// This type exists so R12's table has exactly one home in Domain. The raw-string → enum dictionary
/// is the other half of R12 and lives in Infrastructure, because it is provider vocabulary; this is
/// the half that is a business rule.
/// </remarks>
public static class ShowStatusRules
{
    /// <summary>
    /// True when the show's airing state means no further episodes are expected:
    /// <see cref="ShowStatus.Ended"/> or <see cref="ShowStatus.Canceled"/>.
    /// </summary>
    /// <remarks>
    /// <see cref="ShowStatus.Unknown"/> is deliberately excluded — failing closed is the point
    /// (SPEC R12). <see cref="ShowStatus.ToBeDetermined"/> is excluded because a renewal is still
    /// possible, so promoting a fully-watched show in that state to Finished would be wrong.
    /// <see cref="ShowStatus.InDevelopment"/> is excluded for the same reason: a show that has not
    /// yet begun airing may still air, so it can never be auto-finished.
    /// </remarks>
    public static bool NoMoreEpisodesExpected(ShowStatus status) =>
        status is ShowStatus.Ended or ShowStatus.Canceled;
}
