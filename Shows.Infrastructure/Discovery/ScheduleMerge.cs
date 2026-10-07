namespace Shows.Infrastructure.Discovery;

/// <summary>
/// Folds retained older snapshots into the current one so the buckets can see episodes that already aired
/// (user follow-up, 2026-10-07). Pure — no I/O, no clock — so the reconciliation rules are testable in
/// isolation from the archive that supplies the layers.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a union is needed at all.</b> <c>/schedule/full</c> is forward-only, so a show whose season-1
/// finale aired last week is simply absent from today's response: it is not "filtered out", it is gone.
/// The only place its episodes still exist is a snapshot taken while they were still ahead. Union is
/// therefore recovery, not enrichment.
/// </para>
/// <para>
/// <b>Show metadata: newest wins.</b> The current response is the freshest description of a show, so its
/// entry — the <c>CatalogShow</c> and the network/country facets together — replaces an older layer's
/// wholesale. A show present only in an old layer keeps that layer's metadata: stale, but the only record
/// there is, and it is what makes the show findable at all.
/// </para>
/// <para>
/// <b>Episodes: newest non-null air date wins.</b> An episode keyed on <c>(season, number)</c> can carry
/// different air dates across layers because TVmaze moves episodes. The newer layer is authoritative, so
/// its date replaces the older one — keeping the older date would resurrect a slot the network has
/// already abandoned. A newer <em>null</em> does not clear an older date, though: TVmaze more often omits
/// a field than deliberately withdraws one, and losing a known date to a parsing gap is the worse error.
/// Title and type follow the same rule.
/// </para>
/// </remarks>
internal static class ScheduleMerge
{
    /// <summary>
    /// Merge the current snapshot with older layers.
    /// </summary>
    /// <param name="current">The snapshot just loaded — the freshest information.</param>
    /// <param name="olderLayersNewestFirst">
    /// Parsed older snapshots, newest first. Order matters: applying them oldest-first lets each newer
    /// layer correct the one before it.
    /// </param>
    public static IReadOnlyList<DiscoveryEntry> Merge(
        IReadOnlyList<DiscoveryEntry> current,
        IReadOnlyList<IReadOnlyList<DiscoveryEntry>> olderLayersNewestFirst)
    {
        if (olderLayersNewestFirst.Count == 0)
        {
            return current;
        }

        var byShow = new Dictionary<int, MergedShow>();

        // Oldest first, so a newer layer overwrites the corrections of an older one rather than the reverse.
        for (var i = olderLayersNewestFirst.Count - 1; i >= 0; i--)
        {
            Apply(olderLayersNewestFirst[i], byShow);
        }

        Apply(current, byShow);

        return byShow.Values
            .Select(m => m.ToEntry())
            .ToList();
    }

    private static void Apply(IReadOnlyList<DiscoveryEntry> layer, Dictionary<int, MergedShow> byShow)
    {
        foreach (var entry in layer)
        {
            if (!byShow.TryGetValue(entry.TvmazeId, out var merged))
            {
                merged = new MergedShow();
                byShow[entry.TvmazeId] = merged;
            }

            // Newest layer wins outright on the show itself: it is the freshest description TVmaze offered,
            // and one entry carries the facets too, so there is nothing to reconcile field by field here.
            merged.Latest = entry;

            foreach (var episode in entry.Episodes)
            {
                var key = (episode.Season, episode.Number);
                if (!merged.Episodes.TryGetValue(key, out var existing))
                {
                    merged.Episodes[key] = episode;
                    continue;
                }

                merged.Episodes[key] = existing with
                {
                    AirDate = episode.AirDate ?? existing.AirDate,
                    Type = episode.Type ?? existing.Type,
                    Title = episode.Title ?? existing.Title,
                };
            }
        }
    }

    private sealed class MergedShow
    {
        /// <summary>The newest layer's entry for this show — the base the merged episode set is grafted onto.</summary>
        public DiscoveryEntry? Latest { get; set; }

        public Dictionary<(int Season, int? Number), EpisodeFact> Episodes { get; } = [];

        public DiscoveryEntry ToEntry()
        {
            // Latest is always set: a MergedShow only exists because some layer carried the show.
            var baseEntry = Latest ?? throw new InvalidOperationException("A merged show must carry an entry.");
            return baseEntry with { Episodes = Episodes.Values.ToList() };
        }
    }
}
