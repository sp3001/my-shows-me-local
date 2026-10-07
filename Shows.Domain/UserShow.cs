namespace Shows.Domain;

/// <summary>
/// Everything the user decided about one tracked show. Together with <see cref="EpisodeWatch"/> and
/// <see cref="UiSeasonState"/>, this is the entire irreplaceable content of the database (SPEC P4).
/// </summary>
/// <remarks>
/// <para>
/// It shares its primary key with <see cref="TrackedShow"/> rather than carrying its own — the
/// relationship is 1:1 and a second key would be a second thing to keep in sync (ADR-024).
/// </para>
/// <para>
/// <b>Untracking is a logical delete.</b> Nothing is removed: <see cref="DeletedAt"/> is set and the
/// show's seasons, episodes and watch rows all survive, so re-adding restores the exact watched
/// state. Every read path filters <c>DeletedAt is null</c>, in the repository, so a caller cannot
/// forget (SPEC R8).
/// </para>
/// </remarks>
public sealed class UserShow
{
    /// <summary>Primary key and foreign key at once.</summary>
    public long TrackedShowId { get; set; }

    /// <summary>Kept while soft-deleted, so a restore has something to restore to.</summary>
    public ShowCategory Category { get; set; }

    public DateTimeOffset AddedAt { get; set; }
    public DateTimeOffset CategoryChangedAt { get; set; }

    /// <summary>Set by the import when watched-state reconstruction was a guess; cleared by the user.</summary>
    public bool NeedsReview { get; set; }

    /// <summary>
    /// Improvement 18: the user pinned this show to the top of the Started list, above the active sort.
    /// User data (SPEC P4), so it lives here and not in a UI preference like the sort choice — it must
    /// survive a relaunch and a rebuild. Only the Started list offers pinning; the flag is stored regardless
    /// of category so a show moved out of Started keeps its pin if it ever returns.
    /// </summary>
    public bool IsPinned { get; set; }

    /// <summary>
    /// The user's own rating for this show, from 1 to 10. Null when the user has not rated it. Distinct
    /// from <see cref="TrackedShow.RatingAverage"/>, which is TVmaze's catalog rating and is never the
    /// user's (SPEC P4). User data, so it lives here and survives a rebuild.
    /// </summary>
    public int? UserRating { get; set; }

    /// <summary>Non-null means untracked. NULL means actively tracked.</summary>
    public DateTimeOffset? DeletedAt { get; set; }

    /// <summary>
    /// The show this decision belongs to. The 1:1 inverse of <see cref="TrackedShow.User"/>; EF
    /// requires the dependent side of a shared-primary-key relationship to carry the reference. Plain
    /// C# — Domain stays free of persistence attributes.
    /// </summary>
    public TrackedShow? Show { get; set; }
}
