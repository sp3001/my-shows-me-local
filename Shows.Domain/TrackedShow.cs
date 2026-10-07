namespace Shows.Domain;

/// <summary>
/// A show the user tracks. The only show-shaped entity in v1 — there is no <c>Show</c> type and no
/// catalog table (ADR-020, SPEC P2): a search hit or a discovery-feed entry is never persisted.
/// </summary>
/// <remarks>
/// <para>
/// Field names follow the TVmaze payload (SPEC P1). The user's tracking decision is a separate
/// 1:1 row in <see cref="UserShow"/> (ADR-024), which is what makes this entity entirely rebuildable
/// from the source tables (SPEC P3/P4). The two are composed here so the UI sees one aggregate
/// (<c>ARCHITECTURE.md</c> §3) rather than reaching across two tables for <see cref="Category"/>.
/// </para>
/// <para>
/// Plain C#: no EF attributes, no JSON attributes, no provider URL columns (SPEC P5 — a TVmaze or
/// IMDb URL is a function of its id), and no <c>MyShowsId</c> anywhere (ADR-019).
/// </para>
/// </remarks>
public sealed class TrackedShow
{
    /// <summary>Client-generated surrogate key (SPEC R13). Never re-minted.</summary>
    public long Id { get; set; }

    /// <summary>The natural key. A tracked show always has one (SPEC P1). TVmaze's id space is int.</summary>
    public int TvmazeId { get; set; }

    /// <summary>Original title.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Russian title from the import or IMDb akas. Not a TVmaze field.</summary>
    public string? TitleLocalized { get; set; }

    /// <summary>
    /// TVmaze <c>type</c> — Scripted / Animated / Documentary / Reality. Kept as text because it
    /// carries no behaviour, unlike status (SPEC R12).
    /// </summary>
    public string? ShowType { get; set; }

    public string? Language { get; set; }

    /// <summary>The internal enum, never the raw provider string (SPEC R12).</summary>
    public ShowStatus Status { get; set; }

    /// <summary>TVmaze <c>runtime</c> — frequently null.</summary>
    public int? RuntimeMinutes { get; set; }

    /// <summary>TVmaze <c>averageRuntime</c> — usually the usable one when runtime is null.</summary>
    public int? AverageRuntimeMinutes { get; set; }

    /// <summary>Full date, not a year.</summary>
    public DateOnly? PremiereDate { get; set; }

    public DateOnly? EndDate { get; set; }

    /// <summary>Authored data with no derivation rule, so it is stored (SPEC P5).</summary>
    public string? OfficialSite { get; set; }

    public TimeOnly? ScheduleTime { get; set; }

    /// <summary>Comma list of day names, from TVmaze <c>schedule.days</c>.</summary>
    public string? ScheduleDays { get; set; }

    /// <summary>TVmaze's own rating — catalog data, never the user's.</summary>
    public double? RatingAverage { get; set; }

    /// <summary>Mutually exclusive with <see cref="WebchannelName"/>.</summary>
    public string? NetworkName { get; set; }

    public string? WebchannelName { get; set; }

    /// <summary>
    /// The show's country label for display. TVmaze carries a country only <em>inside</em> the broadcaster
    /// (<c>network.country</c> / <c>webChannel.country</c>), and a streaming broadcaster very often has none
    /// at all — while <see cref="Language"/> is nearly always filled. So this is the broadcaster's country
    /// when TVmaze has one, otherwise the language (improvement 21): a Korean drama on Netflix with no
    /// country object reads "Korean" rather than nothing. The rule lives in
    /// <c>TvmazeJsonMapper.BroadcasterCountry</c> and is applied once, at the mapped layer — the UI reads
    /// this and never re-derives it. Null only when TVmaze has neither.
    /// </summary>
    public string? Country { get; set; }

    /// <summary><c>tt</c> + digits. Null until id resolution fills it in.</summary>
    public string? ImdbId { get; set; }

    public int? TheTVDbId { get; set; }

    public string? PosterUrl { get; set; }
    public string? PosterMediumUrl { get; set; }

    /// <summary>Plain text — the mapper strips TVmaze's HTML; raw markup stays in the source table.</summary>
    public string? Description { get; set; }

    /// <summary>When the mapper last wrote this row.</summary>
    public DateTimeOffset? MappedAt { get; set; }

    // ---- Derived caches. Recomputed from episode x episode_watch, never authored. ----

    public int WatchedRegular { get; set; }
    public int TotalRegular { get; set; }
    public bool HasUnwatchedSpecials { get; set; }
    public bool AutoFinishEligible { get; set; }

    // ---- The user's side of the aggregate (ADR-024). ----

    /// <summary>
    /// The tracking decision. Null only while a show is being created, before the
    /// <c>user_show</c> row exists; every read path returns it populated.
    /// </summary>
    public UserShow? User { get; set; }

    /// <summary>
    /// Throws when <see cref="User"/> is missing rather than defaulting to a category. A silent
    /// fallback would render a show in the wrong tab, which is exactly the invisible failure the
    /// 1:1 split is meant to avoid; a missing <c>user_show</c> row is a bug (SPEC P4), not a state.
    /// </summary>
    public ShowCategory Category => RequireUser().Category;

    public DateTimeOffset AddedAt => RequireUser().AddedAt;
    public DateTimeOffset CategoryChangedAt => RequireUser().CategoryChangedAt;
    public bool NeedsReview => RequireUser().NeedsReview;

    /// <summary>Improvement 18: whether the user pinned this show to the top of the Started list.</summary>
    public bool IsPinned => RequireUser().IsPinned;

    /// <summary>
    /// The user's own 1-10 rating (null when unset). User data (SPEC P4), so it lives on
    /// <see cref="UserShow"/>; TVmaze's catalog rating is <see cref="RatingAverage"/> and is never this.
    /// </summary>
    public int? UserRating => RequireUser().UserRating;

    /// <summary>True when the show is untracked (soft-deleted), not when it is absent.</summary>
    public bool IsDeleted => RequireUser().DeletedAt is not null;

    private UserShow RequireUser() =>
        User ?? throw new InvalidOperationException(
            $"Show {Id} (tvmaze {TvmazeId}) has no user_show row. Every tracked show must have one "
            + "(SPEC P4); a tracked_show without a user_show is an invisible show that no tab can list.");

    public List<Season> Seasons { get; } = [];
    public List<Episode> Episodes { get; } = [];
    public List<Genre> Genres { get; } = [];
}
