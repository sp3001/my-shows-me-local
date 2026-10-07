namespace Shows.Application;

/// <summary>
/// Mints surrogate keys in-process (SPEC R13, ADR-026/ADR-027).
/// </summary>
/// <remarks>
/// <para>
/// One seam, injected, so tests can make ids deterministic and assert the mapper does not regenerate
/// them. The layout lives in Infrastructure so Domain and Application stay dependency-free; the
/// encoding itself is owned by <c>SCHEMA.md</c> §1a and is not restated here.
/// </para>
/// <para>
/// The reason it is a seam at all is concrete: <c>--promote</c> and the mapper create
/// <c>tracked_show</c>, <c>season</c> and <c>episode</c> in one transaction and need the child ids
/// before <c>SaveChanges</c> to build <c>episode_watch</c> rows. A store-generated key forces an
/// intermediate flush in the middle of the import's transaction.
/// </para>
/// </remarks>
public interface IIdGenerator
{
    /// <summary>A positive, strictly increasing 64-bit id. Thread-safe; the app mints from UI and HTTP callback threads alike.</summary>
    long NextId();
}
