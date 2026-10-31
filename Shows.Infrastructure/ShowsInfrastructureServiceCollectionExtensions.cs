using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Shows.Application;
using Shows.Infrastructure.Configuration;
using Shows.Infrastructure.Http;
using Shows.Infrastructure.Persistence;
using Shows.Infrastructure.Persistence.Repositories;
using Shows.Infrastructure.Tvmaze;

namespace Shows.Infrastructure;

/// <summary>
/// Composition entry point for Infrastructure. Called by the MAUI app and by every job — the only
/// place UI may touch Infrastructure is the composition root (<c>ARCHITECTURE.md</c> §2).
/// </summary>
public static class ShowsInfrastructureServiceCollectionExtensions
{
    /// <summary>The three named clients. Routing is decided by which of these a caller asks for.</summary>
    public static readonly string[] NamedClients = ["Tvmaze", "MyShows", "Imdb"];

    /// <summary>
    /// The single definition of how the SQLite provider is configured for <see cref="ShowsDbContext"/>:
    /// snake-case naming, the PRAGMA interceptor, and the EF default of single-query (NOT a global
    /// SplitQuery). SplitQuery was once set here to help the details read, but it broke the mapper —
    /// loading a collection explicitly (<c>Collection(...).LoadAsync</c> in <c>SyncGenresAsync</c>) on an
    /// entity already tracked from a split query throws NullReferenceException in EF Core 8's
    /// <c>ClrICollectionAccessor</c> (the add → untrack → re-add crash). Only the one two-collection read
    /// needs splitting, and it opts in with <c>.AsSplitQuery()</c> at
    /// <c>TrackedShowRepository.FindByIdWithGenresAsync</c>. Exposed so tests configure a context exactly
    /// as production does and cannot drift from it.
    /// </summary>
    public static DbContextOptionsBuilder<TContext> ConfigureShowsSqlite<TContext>(
        DbContextOptionsBuilder<TContext> options, string connectionString)
        where TContext : DbContext =>
        (DbContextOptionsBuilder<TContext>)options.UseSqlite(connectionString)
            .UseSnakeCaseNamingConvention()
            .AddInterceptors(new SqlitePragmaInterceptor());

    public static IServiceCollection AddShowsInfrastructure(
        this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<ShowsOptions>(configuration.GetSection(ShowsOptions.SectionName));

        services.AddSingleton<DataPaths>();

        // The EF context over SQLite (ARCHITECTURE.md §3). The connection string comes from DataPaths so
        // the database lives under the one base directory (SPEC P4 — "back up this directory" stays
        // true). Snake-case naming and the PRAGMA interceptor are configured here, once; the
        // ValueGenerated.Never convention lives on the context itself (SCHEMA.md §1a).
        static void ConfigureDb(IServiceProvider sp, DbContextOptionsBuilder options)
        {
            var paths = sp.GetRequiredService<DataPaths>();
            ConfigureShowsSqlite<ShowsDbContext>(
                (DbContextOptionsBuilder<ShowsDbContext>)options,
                new SqliteConnectionStringBuilder { DataSource = paths.DatabasePath }.ToString());
        }

        // Both the scoped context (repositories, unit of work) and a factory are registered. The factory
        // exists for the singleton WriterLease, which must open short-lived contexts of its own rather
        // than be captive to one scoped context for the process lifetime (ADR-028).
        services.AddDbContextFactory<ShowsDbContext>(ConfigureDb);
        services.AddDbContext<ShowsDbContext>(ConfigureDb);

        // Ids are minted in-process and never re-minted (ADR-026/ADR-027), so the generator is a
        // process-wide singleton holding the last-timestamp state. The lease is a singleton too — it
        // coordinates across the whole process, not per request.
        services.AddSingleton<IIdGenerator, IdGenerator>();
        services.AddSingleton<IWriterLease, SqliteWriterLease>();
        services.AddSingleton<ShowsDatabaseMigrator>();
        // Startup http_cache housekeeping (purge expired validator-less rows). Singleton — it opens its own
        // context from the factory, like the migrator, and runs once at app start rather than as a job.
        services.AddSingleton<HttpCacheMaintenance>();

        // Repositories and the unit of work (step 1.4). Scoped with the context they share. The
        // deleted_at IS NULL predicate lives in ActiveShowFilter, applied by TrackedShowRepository —
        // nowhere else (SPEC R8).
        services.AddScoped<ITrackedShowRepository, TrackedShowRepository>();
        services.AddScoped<IEpisodeRepository, EpisodeRepository>();
        services.AddScoped<IUserShowRepository, UserShowRepository>();
        services.AddScoped<IUiStateRepository, UiStateRepository>();
        services.AddScoped<IUnitOfWork, UnitOfWork>();

        // The single enrichment mapper (SPEC R11, ADR-023). Scoped with the context it writes through.
        // The fetch seam (ITvmazeShowSource, the TVmaze provider) is registered below, so GetService now
        // resolves it and the mapper's MapShowAsync(int) path is live. The DTO overload remains for
        // callers that already hold the payload (a job that owns fetching).
        //
        // Registered as the CONCRETE type first, then exposed as IShowMapper over the same instance: the
        // DTO overload MapShowAsync(TvmazePayload) is concrete-only (TvmazePayload is an Infrastructure
        // type Application cannot name), so --promote's Promoter depends on the concrete mapper while the
        // UI use cases depend on the interface — one instance, both seams.
        services.AddScoped<TvmazeShowMapper>(sp => new TvmazeShowMapper(
            sp.GetRequiredService<ShowsDbContext>(),
            sp.GetRequiredService<IIdGenerator>(),
            sp.GetRequiredService<ILogger<TvmazeShowMapper>>(),
            sp.GetService<ITvmazeShowSource>()));
        services.AddScoped<IShowMapper>(sp => sp.GetRequiredService<TvmazeShowMapper>());

        // The add/untrack use cases (step 3.2). Scoped with the context their repositories, mapper and
        // unit of work share. These are the only place tracked_show + user_show are created together
        // (ADR-024), and the only UI-facing write path before the jobs.
        services.AddScoped<Shows.Application.UseCases.AddShowToTrackingUseCase>();
        services.AddScoped<Shows.Application.UseCases.UntrackShowUseCase>();

        // The search / discovery read use cases (steps 4.1 / 4.2). Scoped with the provider and the
        // repository they read through. Stateless reads (SPEC R8a) — they write nothing but a transport-
        // cache entry — but they still share the scoped context, so they are scoped, not singletons. The
        // ViewModels resolve them from a per-operation scope (the VMs live on the singleton root).
        services.AddScoped<Shows.Application.UseCases.SearchShowsUseCase>();
        // Discovery (user-issues 19): one page of a bucket, marked against user_show. Scoped with the
        // repository it reads; it depends on the IDiscoverySource singleton below (scoped→singleton is fine).
        services.AddScoped<Shows.Application.UseCases.GetDiscoveryPageUseCase>();

        // The search-details read (user-issues 3 / improvement 10): one catalog show by TVmaze id with
        // its already-tracked marker, for the lightweight details page an untracked search result opens.
        // Scoped with the provider and repository it reads through (stateless read, SPEC R8a).
        services.AddScoped<Shows.Application.UseCases.GetCatalogShowUseCase>();

        // The untracked-details read WITH episodes (user-issues 19 follow-up): the details page now lists
        // episodes too. Cache-first — a Discovery show is served from the /schedule/full snapshot (no
        // fetch, so it no longer hangs on the unreachable host); a Search show falls back to a live read.
        // Scoped with the provider, the discovery singleton, and the repository it reads through.
        services.AddScoped<Shows.Application.UseCases.GetCatalogDetailsUseCase>();

        // The tracking-UI use cases (Phase 5) and the details read (Phase 6). Scoped with the context
        // their repositories share. RecomputeProgress is a dependency of ToggleWatchedEpisode, so it is
        // registered before the toggle that consumes it; all funnel progress through the one Domain
        // function (ARCHITECTURE §5).
        services.AddScoped<Shows.Application.UseCases.GetCategoryOverviewUseCase>();
        services.AddScoped<Shows.Application.UseCases.GetShowDetailsUseCase>();
        // The "sort by last aired" key for the non-Started lists (Later). Started gets it from the batch
        // read; this thin read supplies it for categories that skip that batch.
        services.AddScoped<Shows.Application.UseCases.GetLastAiredByShowUseCase>();
        // The light card read (My Shows list): seasons + expansion + filtered inline rows, without the
        // full details load. Scoped with the context its repositories share (issue 7).
        services.AddScoped<Shows.Application.UseCases.GetCardSeasonsUseCase>();
        // The batched Started-list read: the same per-card data for every card in five set-based queries
        // instead of one per card (Started list performance — see GetStartedCardsUseCase).
        services.AddScoped<Shows.Application.UseCases.GetStartedCardsUseCase>();
        // The upcoming-episodes view (improvement 4): Started shows with unknown-or-future episodes, batched.
        services.AddScoped<Shows.Application.UseCases.GetUpcomingEpisodesUseCase>();
        // The Updates tab (improvement 25): ended / renewed / provider-updated / recently-watched, over the
        // transition dates the mapper stamps (ADR-038).
        services.AddScoped<Shows.Application.UseCases.GetUpdatesFeedUseCase>();
        services.AddScoped<Shows.Application.UseCases.ChangeCategoryUseCase>();
        services.AddScoped<Shows.Application.UseCases.RecomputeProgressUseCase>();
        services.AddScoped<Shows.Application.UseCases.ToggleWatchedEpisodeUseCase>();
        services.AddScoped<Shows.Application.UseCases.SetSeasonExpandedUseCase>();
        services.AddScoped<Shows.Application.UseCases.SetShowPinnedUseCase>();
        services.AddScoped<Shows.Application.UseCases.SetShowUserRatingUseCase>();

        // The details-page "Fetch localized titles" button (step 7.5). Scoped with the context its
        // repositories share. The target country is read from configuration HERE — Application must not
        // read ShowsOptions, so the composition root supplies it to the use case's constructor string.
        services.AddScoped(sp => new Shows.Application.UseCases.FetchLocalizedTitlesUseCase(
            sp.GetRequiredService<ITrackedShowRepository>(),
            sp.GetRequiredService<ICatalogProvider>(),
            sp.GetRequiredService<IShowMapper>(),
            sp.GetRequiredService<IUnitOfWork>(),
            sp.GetRequiredService<IOptions<ShowsOptions>>().Value.Localization.TargetCountryCode));

        // The details-page "Sync" button (user-issues improvement 7): re-fetch one show and re-run the
        // mapper through the same MapShowAsync(int) fetch path the add path uses. Scoped with the context
        // its repositories share; the mapper (registered above) carries the ITvmazeShowSource fetch seam.
        // No lease — a single-show interactive fetch, like the akas button.
        services.AddScoped<Shows.Application.UseCases.SyncShowUseCase>();

        // The TVmaze catalog provider (step 3.1). Scoped with the http_cache store and the context it
        // reads/writes. Registered as BOTH seams: ICatalogProvider (Application reads) and
        // ITvmazeShowSource (the mapper's fetch path) — one instance serves both. Uses the Tvmaze named
        // client (proxy/throttle/Polly wired there, SPEC R14).
        services.AddScoped<Tvmaze.HttpCacheStore>();
        services.AddScoped<TvmazeCatalogProvider>();
        services.AddScoped<ICatalogProvider>(sp => sp.GetRequiredService<TvmazeCatalogProvider>());
        services.AddScoped<ITvmazeShowSource>(sp => sp.GetRequiredService<TvmazeCatalogProvider>());

        // The Discovery source (user-issues 19). A process-wide SINGLETON: it holds the parsed
        // /schedule/full snapshot (a ~14 MB parse refreshed at most once a day), which must not be rebuilt
        // per scope. It reads the Tvmaze named client (live mode) or a local file (the user's saved
        // sample), and answers queries with cheap in-memory passes. Scoped use cases depend on it.
        //
        // The archive is the dated raw-snapshot store that gives the look-back window a past:
        // /schedule/full only carries today forward, so episodes that already aired are recoverable only
        // from a capture taken while they were ahead. It is stateless over SQLite, so a singleton beside
        // the source that owns the parsed snapshot it feeds.
        services.AddSingleton<Shows.Infrastructure.Discovery.ScheduleArchive>(sp =>
            new Shows.Infrastructure.Discovery.ScheduleArchive(
                sp.GetRequiredService<IDbContextFactory<ShowsDbContext>>(),
                sp.GetRequiredService<ILogger<Shows.Infrastructure.Discovery.ScheduleArchive>>()));
        services.AddSingleton<Shows.Application.IDiscoverySource>(sp => new Shows.Infrastructure.Discovery.ScheduleDiscoverySource(
            sp.GetRequiredService<IHttpClientFactory>(),
            sp.GetRequiredService<IServiceScopeFactory>(),
            sp.GetRequiredService<DataPaths>(),
            sp.GetRequiredService<IOptions<ShowsOptions>>(),
            sp.GetRequiredService<Shows.Infrastructure.Discovery.ScheduleArchive>(),
            sp.GetRequiredService<ILogger<Shows.Infrastructure.Discovery.ScheduleDiscoverySource>>()));

        // The two-phase import's staging writer (step 2.1). Scoped with the context it writes through.
        // Internal type, registered here so the Import job drives it through the public ImportJob seam
        // without the staging tables leaking into Application (they are Infrastructure-only, SPEC P3).
        services.AddScoped<Import.StagingImporter>();

        // The --report CSV exporter (step 2.4). Read-only against the DB; scoped with the context it reads
        // the v_staging_issues view through. Internal, driven through the ImportJob seam.
        services.AddScoped<Import.StagingReportExporter>();

        // The TVmaze id resolver (step 2.3). The --resolve job body: a resumable/capped/lease-guarded
        // bulk writer over the provider's /lookup?imdb= seam. Internal, driven through the ImportJob seam.
        // ShowsOptions is unwrapped from IOptions for the resolver's cap and heartbeat timings (same as
        // the scraper).
        services.AddScoped(sp => new Import.TvmazeIdResolver(
            sp.GetRequiredService<ShowsDbContext>(),
            sp.GetRequiredService<ICatalogProvider>(),
            sp.GetRequiredService<IWriterLease>(),
            sp.GetRequiredService<IOptions<ShowsOptions>>().Value,
            sp.GetRequiredService<ILogger<Import.TvmazeIdResolver>>()));

        // The --promote job body (step 2.6): the two-phase import's second phase, moving reviewed
        // staging rows into the mapped + user tables. Internal, driven through the ImportJob seam. It
        // needs the CONCRETE mapper (the DTO overload) and the fetch seam (it owns fetching so the
        // network happens outside each show's transaction). ShowsOptions is unwrapped for the heartbeat.
        services.AddScoped(sp => new Import.Promoter(
            sp.GetRequiredService<ShowsDbContext>(),
            sp.GetRequiredService<TvmazeShowMapper>(),
            sp.GetRequiredService<ITvmazeShowSource>(),
            sp.GetRequiredService<IIdGenerator>(),
            sp.GetRequiredService<IWriterLease>(),
            sp.GetRequiredService<IOptions<ShowsOptions>>().Value,
            sp.GetRequiredService<ILogger<Import.Promoter>>()));

        // The ImdbImport job body (step 7.4). The downloader is the network seam over the Imdb named
        // client (it reuses a dataset already on disk, so a re-load never re-downloads); the importer is
        // the lease-guarded three-pass bulk loader. Both internal, driven through the ImdbImportJob seam.
        // ShowsOptions is unwrapped for the heartbeat and the dataset BaseUrl; DataPaths for the file dir.
        services.AddScoped<Import.IImdbDatasetDownloader, Import.ImdbDatasetDownloader>();
        services.AddScoped(sp => new Import.ImdbImporter(
            sp.GetRequiredService<ShowsDbContext>(),
            sp.GetRequiredService<Import.IImdbDatasetDownloader>(),
            sp.GetRequiredService<IWriterLease>(),
            sp.GetRequiredService<IIdGenerator>(),
            sp.GetRequiredService<DataPaths>(),
            sp.GetRequiredService<IOptions<ShowsOptions>>().Value,
            sp.GetRequiredService<ILogger<Import.ImdbImporter>>()));

        // The IMDb-id scraper (step 2.2). The fetcher is the network seam over the MyShows named client;
        // the scraper is the resumable/capped/lease-guarded bulk writer over it. Both internal, driven
        // through the public ScrapeJob seam. ShowsOptions is unwrapped from IOptions for the scraper's cap
        // and heartbeat timings.
        services.AddScoped<Import.IMyShowsPageFetcher, Import.MyShowsPageFetcher>();
        services.AddScoped(sp => new Import.ImdbIdScraper(
            sp.GetRequiredService<ShowsDbContext>(),
            sp.GetRequiredService<Import.IMyShowsPageFetcher>(),
            sp.GetRequiredService<IWriterLease>(),
            sp.GetRequiredService<IOptions<ShowsOptions>>().Value,
            sp.GetRequiredService<ILogger<Import.ImdbIdScraper>>()));

        // The shared refresh-job body (steps 7.1 EpisodeRefresh + 7.2 StatusRecheck). Scoped with the
        // context it writes through; it needs the CONCRETE mapper (MapShowAsync(int) fetch path) and the
        // repository for the shared selection query. ShowsOptions is unwrapped for the cap and heartbeat.
        services.AddScoped(sp => new Jobs.ShowRefreshJob(
            sp.GetRequiredService<ShowsDbContext>(),
            sp.GetRequiredService<ITrackedShowRepository>(),
            sp.GetRequiredService<TvmazeShowMapper>(),
            sp.GetRequiredService<IWriterLease>(),
            sp.GetRequiredService<IIdGenerator>(),
            sp.GetRequiredService<IOptions<ShowsOptions>>().Value,
            sp.GetRequiredService<ILogger<Jobs.ShowRefreshJob>>()));

        // The Enrichment --akas job body (step 7.5). Scoped with the context it writes through; needs the
        // provider (the /akas fetch), the CONCRETE mapper (MapAkasAsync is concrete-only — ShowAka is an
        // Application type but the method is not on IShowMapper), and the repository for the selection.
        // ShowsOptions is unwrapped for the cap, heartbeat, and the target-country pick.
        services.AddScoped(sp => new Jobs.AkasJob(
            sp.GetRequiredService<ShowsDbContext>(),
            sp.GetRequiredService<ITrackedShowRepository>(),
            sp.GetRequiredService<ICatalogProvider>(),
            sp.GetRequiredService<TvmazeShowMapper>(),
            sp.GetRequiredService<IWriterLease>(),
            sp.GetRequiredService<IIdGenerator>(),
            sp.GetRequiredService<IOptions<ShowsOptions>>().Value,
            sp.GetRequiredService<ILogger<Jobs.AkasJob>>()));

        // The RefreshUpdated job body: fetch TVmaze's /updates/shows map once and re-map only the tracked
        // shows whose provider timestamp moved. Scoped with the context it writes through; needs the
        // provider (the bulk map fetch), the CONCRETE mapper (the MapShowAsync(int) fetch path and the
        // GetProviderUpdatedAsync source read), and the repository for the active selection. ShowsOptions
        // is unwrapped for the cap and heartbeat.
        services.AddScoped(sp => new Jobs.RefreshUpdatedShowJob(
            sp.GetRequiredService<ShowsDbContext>(),
            sp.GetRequiredService<ITrackedShowRepository>(),
            sp.GetRequiredService<ICatalogProvider>(),
            sp.GetRequiredService<TvmazeShowMapper>(),
            sp.GetRequiredService<IWriterLease>(),
            sp.GetRequiredService<IIdGenerator>(),
            sp.GetRequiredService<IOptions<ShowsOptions>>().Value,
            sp.GetRequiredService<ILogger<Jobs.RefreshUpdatedShowJob>>()));

        // The Enrichment --imdb job body (step 7.3). No provider — the IMDb data is already in imdb_*
        // (loaded by 7.4). Needs the CONCRETE mapper (EnrichFromImdbAsync is concrete-only, reading the
        // source tables) and the repository for the active-with-imdb-id selection.
        services.AddScoped(sp => new Jobs.ImdbEnrichmentJob(
            sp.GetRequiredService<ShowsDbContext>(),
            sp.GetRequiredService<ITrackedShowRepository>(),
            sp.GetRequiredService<TvmazeShowMapper>(),
            sp.GetRequiredService<IWriterLease>(),
            sp.GetRequiredService<IIdGenerator>(),
            sp.GetRequiredService<IOptions<ShowsOptions>>().Value,
            sp.GetRequiredService<ILogger<Jobs.ImdbEnrichmentJob>>()));

        // The Enrichment --country job body (improvement 21). No provider: it re-derives the country from
        // the payloads already in tvmaze_show.raw_json, so it needs only the CONCRETE mapper (the re-map
        // reads a source table) and the repository for the active selection.
        services.AddScoped(sp => new Jobs.CountryBackfillJob(
            sp.GetRequiredService<ShowsDbContext>(),
            sp.GetRequiredService<ITrackedShowRepository>(),
            sp.GetRequiredService<TvmazeShowMapper>(),
            sp.GetRequiredService<IWriterLease>(),
            sp.GetRequiredService<IIdGenerator>(),
            sp.GetRequiredService<IOptions<ShowsOptions>>().Value,
            sp.GetRequiredService<ILogger<Jobs.CountryBackfillJob>>()));

        // The three named clients, each with its own proxy, throttle and User-Agent. This is the one
        // place Proxy and Http are read (ARCHITECTURE.md §4); no caller builds its own HttpClient and
        // no caller inspects a URL to learn how it will be routed (SPEC R14).
        foreach (var name in NamedClients)
        {
            // Keyed so each client gets its OWN throttle state. A single shared ThrottleState would
            // serialise TVmaze against myshows against IMDb and apply one host's interval to all three.
            services.AddKeyedSingleton<ThrottleState>(name);

            var clientBuilder = services.AddHttpClient(name, (sp, client) =>
            {
                var options = sp.GetRequiredService<IOptions<ShowsOptions>>().Value;
                var service = options.ForClient(name);
                if (!string.IsNullOrWhiteSpace(service.BaseUrl))
                {
                    client.BaseAddress = new Uri(service.BaseUrl);
                }

                client.Timeout = TimeSpan.FromSeconds(options.Http.RequestTimeoutSeconds);

                if (!string.IsNullOrWhiteSpace(service.UserAgent))
                {
                    client.DefaultRequestHeaders.TryAddWithoutValidation(
                        "User-Agent", service.UserAgent);
                }
            })
            .ConfigurePrimaryHttpMessageHandler(sp =>
            {
                var options = sp.GetRequiredService<IOptions<ShowsOptions>>().Value;
                var proxy = ProxyConfig.CreateProxy(options.Proxy.ForClient(name));

                // SocketsHttpHandler, not the default HttpClientHandler: ConnectTimeout only exists on
                // the former, and it is the R14 defence that makes a FILTERED proxy port fail fast
                // instead of waiting out the whole request timeout and looking like a slow provider
                // (DATA_ANALYSIS.md §9.6).
                return new SocketsHttpHandler
                {
                    Proxy = proxy,

                    // Explicit direct when the proxy value is empty — UseProxy=false, never "inherit
                    // the machine's settings" (DATA_ANALYSIS.md §9.5, SPEC R14).
                    UseProxy = proxy is not null,

                    ConnectTimeout = TimeSpan.FromSeconds(options.Http.ConnectTimeoutSeconds),

                    // The throttle handler serialises per host, so connection pooling is not contended.
                    PooledConnectionLifetime = TimeSpan.FromMinutes(10),
                };
            })
            .AddHttpMessageHandler(sp =>
            {
                var options = sp.GetRequiredService<IOptions<ShowsOptions>>().Value;
                var service = options.ForClient(name);
                return new ThrottlingHandler(
                    name,
                    sp.GetRequiredKeyedService<ThrottleState>(name),
                    service.MinRequestIntervalMs,
                    sp.GetRequiredService<ILoggerFactory>().CreateLogger<ThrottlingHandler>());
            });

            // Polly retry + circuit breaker on TVmaze only (ARCHITECTURE.md §5). The other two hosts are
            // reachable directly and do not need it. The policies handle only HTTP 5xx/408/429 responses,
            // never a transport exception, so a dead proxy fails fast instead of multiplying the connect
            // timeout (SPEC R14, IMPLEMENTATION_PLAN step 3.1).
            if (name == "Tvmaze")
            {
                clientBuilder.AddPolicyHandler(TvmazePolicies.Combined());
            }
        }

        // The in-process job runner (the Manage page's seam). Singleton: it holds no per-run state — it
        // forwards a JobKind to the console entry points, which open their own scope and drive the scoped
        // job bodies. It depends only on the root IServiceProvider (to create those scopes) and a logger,
        // so it is safe to live for the process lifetime. Exposed as the Application seam IJobRunner so the
        // UI never names Infrastructure (ARCHITECTURE.md §2).
        services.AddSingleton<IJobRunner, Jobs.JobRunner>();

        return services;
    }
}
