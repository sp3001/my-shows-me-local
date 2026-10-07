using System.Globalization;
using System.Text.Json;
using Shows.Application;

namespace Shows.Infrastructure.Discovery;

/// <summary>
/// One show aggregated out of the <c>/schedule/full</c> snapshot, with the episode facts needed to
/// classify it into a Discovery bucket and the facet strings needed to filter it (user-issues 19).
/// </summary>
/// <remarks>
/// The schedule is a flat array of <b>episode</b> objects, each embedding its <b>show</b>; a show with
/// twelve scheduled episodes appears twelve times. <see cref="ScheduleParser"/> aggregates by show id so
/// each show is one entry. Buckets are <b>not</b> baked in at parse time — they depend on the current
/// date and the configured window, so they are computed per query (see <see cref="DiscoveryIndex"/>).
/// That keeps a cached snapshot valid across days: the expensive 14 MB parse happens once, the cheap
/// bucket/filter pass runs per query.
/// </remarks>
internal sealed record DiscoveryEntry(
    int TvmazeId,
    CatalogShow Show,
    IReadOnlyList<EpisodeFact> Episodes,
    string? NetworkName,
    string? CountryName,
    string? CountryCode);

/// <summary>
/// The episode facts Discovery needs: which season it belongs to, when it airs, and its title. The title
/// is carried so the Discovery details page can render the episode list straight from the cached snapshot
/// (user-issues 19 follow-up) without a live fetch. Everything else about an episode is irrelevant to
/// bucketing, so it is not retained — the snapshot stays small.
/// </summary>
internal sealed record EpisodeFact(int Season, int? Number, DateOnly? AirDate, string? Type, string? Title);

/// <summary>
/// Pure parse from the <c>/schedule/full</c> JSON to <see cref="DiscoveryEntry"/> rows. No I/O, no clock
/// — so the whole Discovery data path is testable against a recorded fixture with no network.
/// </summary>
/// <remarks>
/// Navigates <see cref="JsonElement"/> directly (the same reason <c>TvmazeJsonMapper</c> does): TVmaze
/// adds fields without notice (DATA_ANALYSIS §8), and the show side is mapped through the one
/// <see cref="Tvmaze.TvmazeJsonMapper.MapCatalogShow"/> so a Discovery card and a Search card carry the
/// identical read model. The episode side only reads season/number/airdate/type.
/// </remarks>
internal static class ScheduleParser
{
    public static IReadOnlyList<DiscoveryEntry> Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        // Aggregate by show id: the first time a show is seen its CatalogShow + facets are captured; every
        // episode row (including repeats of the same show) contributes one EpisodeFact.
        var byShow = new Dictionary<int, Builder>();
        foreach (var episode in doc.RootElement.EnumerateArray())
        {
            if (!episode.TryGetProperty("_embedded", out var embedded)
                || embedded.ValueKind != JsonValueKind.Object
                || !embedded.TryGetProperty("show", out var show)
                || show.ValueKind != JsonValueKind.Object)
            {
                continue; // a schedule row with no embedded show carries nothing Discovery can use
            }

            var idElement = show.TryGetProperty("id", out var id) ? id : default;
            if (idElement.ValueKind != JsonValueKind.Number || !idElement.TryGetInt32(out var tvmazeId))
            {
                continue;
            }

            if (!byShow.TryGetValue(tvmazeId, out var builder))
            {
                // Read the facets while the element is alive — the JsonDocument is disposed once Parse returns.
                // The broadcaster name comes from the same nested-object rule the search read model uses; the
                // country facet is taken off the mapped CatalogShow, which already applied the one display
                // rule (broadcaster country, else language — improvement 21). Facet and card therefore
                // cannot disagree: a show whose card says "Korean" is findable under the Korean facet.
                var network = OptObject(show, "network");
                var webChannel = OptObject(show, "webChannel");
                var catalogShow = Tvmaze.TvmazeJsonMapper.MapCatalogShow(show);

                builder = new Builder(
                    catalogShow,
                    OptString(network, "name") ?? OptString(webChannel, "name"),
                    catalogShow.CountryName,
                    Tvmaze.TvmazeJsonMapper.ReadBroadcasterCountry(show).Code);
                byShow[tvmazeId] = builder;
            }

            builder.Episodes.Add(new EpisodeFact(
                Season: OptInt(episode, "season") ?? 0,
                Number: OptInt(episode, "number"),
                AirDate: ParseDate(OptString(episode, "airdate")),
                Type: OptString(episode, "type"),
                Title: OptString(episode, "name")));
        }

        return byShow.Values
            .Select(b => new DiscoveryEntry(
                b.Show.TvmazeId, b.Show, b.Episodes, b.NetworkName, b.CountryName, b.CountryCode))
            .ToList();
    }

    private sealed record Builder(
        CatalogShow Show,
        string? NetworkName,
        string? CountryName,
        string? CountryCode)
    {
        public List<EpisodeFact> Episodes { get; } = [];
    }

    // ---- tolerant readers (TVmaze adds/removes fields without notice) ----

    private static JsonElement OptObject(JsonElement el, string name) =>
        el.ValueKind == JsonValueKind.Object
        && el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Object ? v : default;

    private static string? OptString(JsonElement el, string name) =>
        el.ValueKind == JsonValueKind.Object
        && el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int? OptInt(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i)
            ? i
            : null;

    private static DateOnly? ParseDate(string? raw) =>
        DateOnly.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
}
