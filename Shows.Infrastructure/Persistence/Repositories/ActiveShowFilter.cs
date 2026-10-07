using System.Linq.Expressions;
using Shows.Domain;

namespace Shows.Infrastructure.Persistence.Repositories;

/// <summary>
/// The single definition of "actively tracked" (SPEC R8, ADR-024). Every default read path applies
/// this; the <c>*IncludingUntracked</c> variants deliberately do not. Centralising it here is what
/// keeps the predicate in exactly one place — a forgotten filter shows an untracked show in a tab,
/// which is the invisible failure the 1:1 split exists to prevent.
/// </summary>
/// <remarks>
/// Deliberately not an EF global query filter: the jobs must be able to see soft-deleted rows, and
/// that choice has to stay visible at each call site (ARCHITECTURE.md §5).
/// </remarks>
internal static class ActiveShowFilter
{
    public static readonly Expression<Func<TrackedShow, bool>> Predicate =
        show => show.User != null && show.User.DeletedAt == null;
}
