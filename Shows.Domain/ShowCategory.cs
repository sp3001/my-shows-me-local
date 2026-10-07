namespace Shows.Domain;

/// <summary>
/// The user's tracking category. Exactly four (SPEC R1) — there is no <c>Paused</c>; the word
/// "paused" in job descriptions refers to the TVmaze <c>status</c> string, never to a category.
/// </summary>
/// <remarks>
/// The numeric values are the <c>user_show.category_id</c> column values
/// (<c>SCHEMA.md</c> §2 <c>user_show</c>). They are persisted, so reordering the members is a
/// data migration, not a code change.
/// <para>
/// Unlike <see cref="ShowStatus"/> and <see cref="EpisodeType"/>, this enum has no <c>Unknown</c>
/// member: SPEC R1 fixes the set at exactly four, and <c>IMPLEMENTATION_PLAN.md</c> step 0.2 names
/// only those two enums as needing a fail-closed member. A category is always authored by a user
/// action or by the import's explicit mapping, so there is no unmapped-vocabulary case to land on —
/// which is why <c>default(ShowCategory)</c> being 0 is an invalid value rather than a safe one.
/// </para>
/// </remarks>
#pragma warning disable CA1008 // Enums should have zero value - see remark: 0 is deliberately invalid.
public enum ShowCategory
#pragma warning restore CA1008
{
    Started = 1,
    Later = 2,
    Cancelled = 3,
    Finished = 4,
}
