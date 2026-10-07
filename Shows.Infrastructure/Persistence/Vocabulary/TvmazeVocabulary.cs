using Shows.Domain;

namespace Shows.Infrastructure.Persistence.Vocabulary;

/// <summary>
/// The one point of contact between provider vocabulary and internal enums (SPEC R12). Every raw
/// TVmaze string becomes an enum here and nowhere else; an unrecognised value fails closed to
/// <see cref="ShowStatus.Unknown"/> / <see cref="EpisodeType.Unknown"/>, never a guess.
/// </summary>
/// <remarks>
/// <para>
/// Matching is exact and case-sensitive against the stored raw string, per SPEC R12. The dictionary
/// is the only place a vocabulary change lands: adding a TVmaze status is one entry plus a warning
/// log, not a hunt through predicates. The original bug this prevents is a predicate comparing
/// against <c>"Running Series"</c> — a myshows string TVmaze never returns — which matched nothing
/// and would have auto-finished every airing show.
/// </para>
/// <para>
/// <see cref="ShowStatus.Unknown"/> is <c>0</c> and is deliberately excluded from
/// <see cref="ShowStatusRules.NoMoreEpisodesExpected"/>, so an unmapped status can never satisfy the
/// auto-finish test. That is the fail-closed guarantee.
/// </para>
/// </remarks>
public static class TvmazeVocabulary
{
    // Exact, case-sensitive. SPEC R12's table is the authority; this is its only code form.
    private static readonly Dictionary<string, ShowStatus> StatusMap = new(StringComparer.Ordinal)
    {
        ["Running"] = ShowStatus.Running,
        ["Ended"] = ShowStatus.Ended,
        ["Canceled"] = ShowStatus.Canceled,
        ["To Be Determined"] = ShowStatus.ToBeDetermined,
        ["In Development"] = ShowStatus.InDevelopment,
    };

    // TVmaze returns specials as "significant_special" / "insignificant_special" (ADR-034). The
    // significant kind counts toward progress (EpisodeType.Special); the insignificant kind is filler
    // and is excluded (EpisodeType.InsignificantSpecial). The bare "special" value is not something
    // TVmaze returns today, but it is kept as a synonym for the excluded kind so a legacy row (or a
    // hand-authored fixture) still means "not counted".
    private static readonly Dictionary<string, EpisodeType> EpisodeTypeMap = new(StringComparer.Ordinal)
    {
        ["regular"] = EpisodeType.Regular,
        ["significant_special"] = EpisodeType.Special,
        ["insignificant_special"] = EpisodeType.InsignificantSpecial,
        ["special"] = EpisodeType.InsignificantSpecial,
    };

    /// <summary>Maps a raw <c>tvmaze_show.status_raw</c>. Unrecognised → <see cref="ShowStatus.Unknown"/>.</summary>
    public static ShowStatus MapStatus(string? statusRaw) =>
        statusRaw is not null && StatusMap.TryGetValue(statusRaw, out var status)
            ? status
            : ShowStatus.Unknown;

    /// <summary>Maps a raw <c>tvmaze_episode.type_raw</c>. Unrecognised → <see cref="EpisodeType.Unknown"/>.</summary>
    public static EpisodeType MapEpisodeType(string? typeRaw) =>
        typeRaw is not null && EpisodeTypeMap.TryGetValue(typeRaw, out var type)
            ? type
            : EpisodeType.Unknown;

    /// <summary>
    /// True when the raw status is one the dictionary knows. Drives the <c>tvmaze_status_seen.mapped_to</c>
    /// column: a false here is the visible signal that the dictionary needs an edit (SPEC R12).
    /// </summary>
    public static bool IsKnownStatus(string? statusRaw) =>
        statusRaw is not null && StatusMap.ContainsKey(statusRaw);
}
