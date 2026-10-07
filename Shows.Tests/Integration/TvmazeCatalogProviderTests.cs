using System.Net;
using System.Net.Http.Headers;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shows.Application;
using Shows.Infrastructure.Configuration;
using Shows.Infrastructure.Persistence;
using Shows.Infrastructure.Tvmaze;
using Xunit;

// CA2000: the FakeHandler holds no unmanaged resources and the per-test ShowsDbContext is released by
// ClearAllPools in Dispose(); neither needs a deterministic Dispose within the test method's scope.
#pragma warning disable CA2000

namespace Shows.Tests.Integration;

/// <summary>
/// Step 3.1 acceptance, driven entirely by recorded JSON fixtures over a fake HTTP handler — no network
/// (IMPLEMENTATION_PLAN step 3.1: "tests against recorded JSON fixtures, no network"). Covers the
/// payload→source-row mapping (the Black Mirror sample from DATA_ANALYSIS §8), the read-model mapping,
/// the R14 fail-loud rule (transport failure and non-404 errors throw, never return empty), and the
/// cache-revalidation property (a cached response is revalidated with If-None-Match and reused on 304,
/// not refetched).
/// </summary>
public sealed class TvmazeCatalogProviderTests : IDisposable
{
    private readonly string _dbPath;

    public TvmazeCatalogProviderTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"shows-3-1-{Guid.NewGuid():N}.db");
        using var context = CreateContext();
        context.Database.Migrate();
    }

    // ---- payload → source rows (the mapper's fetch seam) ----

    [Fact]
    public async Task FetchAsync_maps_the_black_mirror_payload_verbatim_into_source_rows()
    {
        var handler = new FakeHandler()
            .Json("shows/305", ShowJson)
            .Json("shows/305/episodes?specials=1", EpisodesJson);
        var provider = CreateProvider(handler);

        var payload = await provider.FetchAsync(305, CancellationToken.None);

        Assert.NotNull(payload);
        var show = payload!.Show;
        Assert.Equal(305, show.TvmazeId);
        Assert.Equal("Black Mirror", show.Name);
        Assert.Equal("Running", show.StatusRaw);
        Assert.Null(show.RuntimeMinutes);          // runtime is null at show level ...
        Assert.Equal(62, show.AverageRuntime);     // ... while averageRuntime carries the number
        Assert.Equal("tt2085059", show.ImdbId);
        Assert.Equal("Netflix", show.WebchannelName);
        Assert.Null(show.NetworkName);             // streaming: network null, webChannel set
        Assert.Equal(8.4, show.RatingAverage);
        Assert.Equal("""["Drama","Science-Fiction","Thriller"]""", show.GenresRaw);
        Assert.Contains("Black Mirror", show.RawJson); // verbatim payload captured

        Assert.Equal(3, payload.Episodes.Count);
        var first = payload.Episodes[0];
        Assert.Equal(30902, first.TvmazeEpisodeId);
        Assert.Equal(305, first.TvmazeShowId);
        Assert.Equal("regular", first.TypeRaw);
        Assert.Equal("2011-12-04T21:00:00+00:00", first.Airstamp);
        Assert.Contains("National Anthem", first.RawJson);
    }

    [Fact]
    public async Task FetchAsync_returns_null_for_a_genuine_404()
    {
        var handler = new FakeHandler().Status("shows/999999", HttpStatusCode.NotFound);
        var provider = CreateProvider(handler);

        Assert.Null(await provider.FetchAsync(999999, CancellationToken.None));
    }

    // ---- read-model mapping ----

    [Fact]
    public async Task GetShowAsync_maps_a_catalog_read_model_collapsing_runtime_for_display()
    {
        var handler = new FakeHandler().Json("shows/305", ShowJson);
        var provider = CreateProvider(handler);

        var catalog = await provider.GetShowAsync(305, CancellationToken.None);

        Assert.NotNull(catalog);
        Assert.Equal(305, catalog!.TvmazeId);
        Assert.Equal("Black Mirror", catalog.Title);
        Assert.Equal(2011, catalog.Year);                 // from premiered "2011-12-04"
        Assert.Equal("tt2085059", catalog.ImdbId);
        Assert.Equal(62, catalog.RuntimeMinutes);         // runtime(null) ?? averageRuntime(62) at display
        Assert.Equal(["Drama", "Science-Fiction", "Thriller"], catalog.Genres);
        Assert.False(catalog.IsAlreadyTracked);           // the provider never computes tracking
    }

    [Fact]
    public async Task SearchAsync_unwraps_the_score_envelope_into_catalog_shows()
    {
        // The real /search/shows shape (verified live, LOG step 3.1): an array of {score, show:{…}}
        // wrappers, NOT flat show objects. Mapping a wrapper as a show throws (no "id"); the provider
        // must unwrap it.
        var envelope = """[{"score":9.123,"show":""" + ShowJson + "}]";
        var handler = new FakeHandler().Json("search/shows?q=black", envelope);
        var provider = CreateProvider(handler);

        var results = await provider.SearchAsync("black", CancellationToken.None);

        Assert.Single(results);
        Assert.Equal("Black Mirror", results[0].Title);
        Assert.Equal(305, results[0].TvmazeId);
    }

    [Fact]
    public async Task LookupByImdbIdAsync_returns_the_tvmaze_id()
    {
        var handler = new FakeHandler().Json("lookup/shows?imdb=tt2085059", ShowJson);
        var provider = CreateProvider(handler);

        Assert.Equal(305, await provider.LookupByImdbIdAsync("tt2085059", CancellationToken.None));
    }

    [Fact]
    public async Task LookupByImdbIdAsync_returns_null_on_404()
    {
        var handler = new FakeHandler().Status("lookup/shows?imdb=tt0000000", HttpStatusCode.NotFound);
        var provider = CreateProvider(handler);

        Assert.Null(await provider.LookupByImdbIdAsync("tt0000000", CancellationToken.None));
    }

    // ---- R14: fail loud, never empty ----

    [Fact]
    public async Task A_transport_failure_throws_naming_the_host_rather_than_returning_empty()
    {
        var handler = new FakeHandler().ThrowOn("search/shows?q=anything", new HttpRequestException("connection reset"));
        var provider = CreateProvider(handler);

        var ex = await Assert.ThrowsAsync<TvmazeUnavailableException>(
            () => provider.SearchAsync("anything", CancellationToken.None));
        Assert.Contains("api.tvmaze.com", ex.Message);
    }

    [Fact]
    public async Task A_server_error_throws_rather_than_returning_empty()
    {
        var handler = new FakeHandler().Status("search/shows?q=x", HttpStatusCode.ServiceUnavailable);
        var provider = CreateProvider(handler);

        await Assert.ThrowsAsync<TvmazeUnavailableException>(
            () => provider.SearchAsync("x", CancellationToken.None));
    }

    // ---- /akas (step 7.5) ----

    [Fact]
    public async Task GetAkasAsync_parses_the_country_keyed_shape_in_array_order()
    {
        // The probed live shape (step 7.5): { name, country: { name, code, timezone } } — keys on
        // country.code, NOT a language field. Order is preserved; it is the order the mapper trusts.
        var handler = new FakeHandler().Json("shows/59985/akas", AkasJson);
        var provider = CreateProvider(handler);

        var akas = await provider.GetAkasAsync(59985, CancellationToken.None);

        Assert.Equal(5, akas.Count);
        Assert.Equal("Начало", akas[0].Name);
        Assert.Equal("RU", akas[0].CountryCode);
        Assert.Equal("Russian Federation", akas[0].CountryName);
        Assert.Equal(["RU", "RU", "RU", "CN", "CN"], akas.Select(a => a.CountryCode).ToArray());
    }

    [Fact]
    public async Task GetAkasAsync_returns_empty_on_a_genuine_404_not_a_failure()
    {
        // A show with no akas at all is a real "TVmaze has no akas" -> empty list, NOT a thrown failure
        // (R14 reserves the throw for an unreachable host, not for a genuine empty result).
        var handler = new FakeHandler().Status("shows/123/akas", HttpStatusCode.NotFound);
        var provider = CreateProvider(handler);

        var akas = await provider.GetAkasAsync(123, CancellationToken.None);
        Assert.Empty(akas);
    }

    [Fact]
    public async Task GetAkasAsync_throws_on_a_transport_failure_naming_the_host()
    {
        var handler = new FakeHandler().ThrowOn("shows/305/akas", new HttpRequestException("connection reset"));
        var provider = CreateProvider(handler);

        var ex = await Assert.ThrowsAsync<TvmazeUnavailableException>(
            () => provider.GetAkasAsync(305, CancellationToken.None));
        Assert.Contains("api.tvmaze.com", ex.Message);
    }

    // ---- /updates/shows (the RefreshUpdated job's bulk map) ----

    [Fact]
    public async Task GetShowUpdateTimestampsAsync_parses_the_id_to_updated_map()
    {
        // The endpoint returns { "<tvmazeId>": <unixSeconds> } for every show on the site. Keys are
        // strings, values are numbers; a non-numeric key/value is skipped rather than throwing.
        var handler = new FakeHandler().Json("updates/shows", """
            {"1":1789071819,"305":1767985660,"73305":1704793709,"bad":"x","999":null}
            """);
        var provider = CreateProvider(handler);

        var map = await provider.GetShowUpdateTimestampsAsync(CancellationToken.None);

        Assert.Equal(1789071819, map[1]);
        Assert.Equal(1767985660, map[305]);
        Assert.Equal(1704793709, map[73305]);
        Assert.Equal(3, map.Count); // the malformed key and null value are skipped
    }

    [Fact]
    public async Task GetShowUpdateTimestampsAsync_throws_on_a_transport_failure_naming_the_host()
    {
        // R14: an unreachable host must not collapse to an empty map — the job would then think nothing
        // changed and report a clean run while refreshing nothing.
        var handler = new FakeHandler().ThrowOn("updates/shows", new HttpRequestException("reset"));
        var provider = CreateProvider(handler);

        var ex = await Assert.ThrowsAsync<TvmazeUnavailableException>(
            () => provider.GetShowUpdateTimestampsAsync(CancellationToken.None));
        Assert.Contains("api.tvmaze.com", ex.Message);
    }

    // ---- cache revalidation (the step-3.1 assertion) ----

    [Fact]
    public async Task A_cached_response_is_revalidated_and_reused_not_refetched()
    {
        var handler = new FakeHandler().Json("shows/305", ShowJson, etag: "\"abc123\"");
        var provider = CreateProvider(handler);

        var first = await provider.GetShowAsync(305, CancellationToken.None);
        Assert.NotNull(first);

        // The body is now cached with its ETag.
        using (var check = CreateContext())
        {
            var row = Assert.Single(check.HttpCache.Where(h => h.Url == "shows/305"));
            Assert.Equal("\"abc123\"", row.Etag);
            Assert.Equal(ShowJson, row.Body);
        }

        // Second call: the handler is set to answer 304 and asserts the conditional header arrived.
        handler.SetConditional("shows/305", expectIfNoneMatch: "\"abc123\"");
        var second = await provider.GetShowAsync(305, CancellationToken.None);

        Assert.NotNull(second);
        Assert.Equal(first!.TvmazeId, second!.TvmazeId);
        Assert.Equal(1, handler.RevalidationCount);   // exactly one 304 revalidation happened
        Assert.Equal(2, handler.RequestCount);        // two requests total, but the second carried no body
    }

    // ---- time-based fallback for a validator-less response (improvement 16) ----

    [Fact]
    public async Task A_validator_less_response_is_served_from_the_time_cache_without_a_second_request()
    {
        // No ETag / Last-Modified on the response, so there is nothing to revalidate against.
        var handler = new FakeHandler().Json("shows/305", ShowJson);
        var provider = CreateProvider(handler);

        var first = await provider.GetShowAsync(305, CancellationToken.None);
        Assert.NotNull(first);
        Assert.Equal(1, handler.RequestCount);

        // The body is cached with no validator.
        using (var check = CreateContext())
        {
            var row = Assert.Single(check.HttpCache.Where(h => h.Url == "shows/305"));
            Assert.Null(row.Etag);
            Assert.Null(row.LastModified);
        }

        // Second call within the max age: served straight from the cache, no network at all.
        var second = await provider.GetShowAsync(305, CancellationToken.None);
        Assert.NotNull(second);
        Assert.Equal(first!.TvmazeId, second!.TvmazeId);
        Assert.Equal(1, handler.RequestCount);   // unchanged — the second read never hit the handler
    }

    [Fact]
    public async Task A_validator_less_response_is_refetched_once_it_ages_out()
    {
        var handler = new FakeHandler().Json("shows/305", ShowJson);
        var provider = CreateProvider(handler);

        await provider.GetShowAsync(305, CancellationToken.None);
        Assert.Equal(1, handler.RequestCount);

        // Backdate the cached row beyond the 24h window so the next read must refetch.
        using (var check = CreateContext())
        {
            var row = Assert.Single(check.HttpCache.Where(h => h.Url == "shows/305"));
            row.FetchedAt = DateTimeOffset.UtcNow - TimeSpan.FromHours(25);
            await check.SaveChangesAsync();
        }

        var second = await provider.GetShowAsync(305, CancellationToken.None);
        Assert.NotNull(second);
        Assert.Equal(2, handler.RequestCount);   // aged out → refetched
    }

    [Fact]
    public async Task A_validated_response_is_never_served_from_the_time_cache()
    {
        // Even though the entry is fresh, an ETag means it must be revalidated, not served from the cache.
        var handler = new FakeHandler().Json("shows/305", ShowJson, etag: "\"abc123\"");
        var provider = CreateProvider(handler);

        await provider.GetShowAsync(305, CancellationToken.None);
        Assert.Equal(1, handler.RequestCount);

        // No conditional is armed, so the second request returns a fresh 200 — but crucially it DID go
        // to the network (a validated entry is never short-circuited by the time cache).
        await provider.GetShowAsync(305, CancellationToken.None);
        Assert.Equal(2, handler.RequestCount);
    }


    // ---- helpers ----

    private TvmazeCatalogProvider CreateProvider(FakeHandler handler)
    {
        var context = CreateContext();
        var cache = new HttpCacheStore(
            context, NullLogger<HttpCacheStore>.Instance, Options.Create(new ShowsOptions()));
        var factory = new FakeHttpClientFactory(handler);
        return new TvmazeCatalogProvider(factory, cache, NullLogger<TvmazeCatalogProvider>.Instance);
    }

    private ShowsDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<ShowsDbContext>()
            .UseSqlite(new SqliteConnectionStringBuilder { DataSource = _dbPath }.ToString())
            .UseSnakeCaseNamingConvention()
            .AddInterceptors(new SqlitePragmaInterceptor())
            .Options;
        return new ShowsDbContext(options);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath))
        {
            File.Delete(_dbPath);
        }
    }

    // Recorded Black Mirror show payload (DATA_ANALYSIS.md §8), abbreviated to the fields the mapper reads.
    private const string ShowJson = """
        {"id":305,"url":"http://www.tvmaze.com/shows/305/black-mirror","name":"Black Mirror",
         "type":"Scripted","language":"English","genres":["Drama","Science-Fiction","Thriller"],
         "status":"Running","runtime":null,"averageRuntime":62,
         "premiered":"2011-12-04","ended":null,"officialSite":"https://www.netflix.com/title/70264888",
         "schedule":{"time":"","days":[]},"rating":{"average":8.4},"weight":100,
         "network":null,"webChannel":{"id":1,"name":"Netflix","country":null},
         "externals":{"tvrage":30348,"thetvdb":253463,"imdb":"tt2085059"},
         "image":{"medium":"https://static.tvmaze.com/m/1411764.jpg","original":"https://static.tvmaze.com/o/1411764.jpg"},
         "summary":"<p>Over the last ten years, technology has transformed...</p>","updated":1767985660}
        """;

    private const string EpisodesJson = """
        [
          {"id":30902,"name":"The National Anthem","season":1,"number":1,"type":"regular",
           "airdate":"2011-12-04","airtime":"21:00","airstamp":"2011-12-04T21:00:00+00:00","runtime":60,
           "rating":{"average":7.8},"image":{"medium":"https://static.tvmaze.com/m/1008339.jpg","original":"https://static.tvmaze.com/o/1008339.jpg"},
           "summary":"<p>Prime Minister Michael Callow faces a huge and shocking dilemma...</p>"},
          {"id":30903,"name":"Fifteen Million Merits","season":1,"number":2,"type":"regular",
           "airdate":"2011-12-11","airstamp":"2011-12-11T21:00:00+00:00","runtime":62,
           "summary":"<p>Bing wakes up in a strange virtual world...</p>"},
          {"id":30904,"name":"Special","season":0,"number":1,"type":"special",
           "airdate":"2014-12-16","airstamp":"2014-12-16T00:00:00+00:00"}
        ]
        """;

    // Recorded /akas payload (probed live 2026-09-30, shows/59985/akas): three RU + two CN, keyed on
    // country.code. Abbreviated to the fields the parser reads (name, country.name, country.code).
    private const string AkasJson = """
        [
          {"name":"Начало","country":{"name":"Russian Federation","code":"RU","timezone":"Europe/Moscow"}},
          {"name":"Сначала","country":{"name":"Russian Federation","code":"RU","timezone":"Europe/Moscow"}},
          {"name":"Перезагрузка","country":{"name":"Russian Federation","code":"RU","timezone":"Europe/Moscow"}},
          {"name":"Black Mirror CN 1","country":{"name":"China","code":"CN","timezone":"Asia/Shanghai"}},
          {"name":"Black Mirror CN 2","country":{"name":"China","code":"CN","timezone":"Asia/Shanghai"}}
        ]
        """;

    private sealed class FakeHttpClientFactory(FakeHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            new(handler) { BaseAddress = new Uri("https://api.tvmaze.com/") };
    }

    /// <summary>
    /// A controllable handler: maps a URL fragment to a canned response, records every request, and can
    /// simulate a transport throw or a conditional 304. Keyed on the URL path+query so the provider's
    /// relative URLs resolve the same way they do against the real base address.
    /// </summary>
    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, Func<HttpRequestMessage, HttpResponseMessage>> _responders = new();
        private readonly Dictionary<string, string> _expectIfNoneMatch = new();

        public int RequestCount { get; private set; }

        public int RevalidationCount { get; private set; }

        public FakeHandler Json(string key, string body, string? etag = null)
        {
            _responders[key] = _ =>
            {
                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
                };
                if (etag is not null)
                {
                    response.Headers.ETag = new EntityTagHeaderValue(etag);
                }

                return response;
            };
            return this;
        }

        public FakeHandler Status(string key, HttpStatusCode status)
        {
            _responders[key] = _ => new HttpResponseMessage(status);
            return this;
        }

        public FakeHandler ThrowOn(string key, Exception ex)
        {
            _responders[key] = _ => throw ex;
            return this;
        }

        /// <summary>Make the next response for <paramref name="key"/> a 304, asserting the request carried the given ETag.</summary>
        public void SetConditional(string key, string expectIfNoneMatch)
        {
            _expectIfNoneMatch[key] = expectIfNoneMatch;
            _responders[key] = request =>
            {
                var sent = request.Headers.TryGetValues("If-None-Match", out var values)
                    ? string.Join(",", values)
                    : null;
                if (sent != expectIfNoneMatch)
                {
                    throw new InvalidOperationException(
                        $"Expected If-None-Match '{expectIfNoneMatch}' but request carried '{sent ?? "<none>"}'.");
                }

                RevalidationCount++;
                return new HttpResponseMessage(HttpStatusCode.NotModified);
            };
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            var key = Key(request);
            if (_responders.TryGetValue(key, out var responder))
            {
                return Task.FromResult(responder(request));
            }

            throw new InvalidOperationException($"FakeHandler has no response for '{key}'.");
        }

        private static string Key(HttpRequestMessage request) =>
            request.RequestUri!.ToString().Replace("https://api.tvmaze.com/", string.Empty, StringComparison.Ordinal);
    }
}
