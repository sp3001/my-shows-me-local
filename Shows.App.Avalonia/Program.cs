using System;
using Avalonia;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Shows.App.Avalonia.Services;
using Shows.App.Avalonia.ViewModels;
using Shows.Infrastructure;
using Shows.Infrastructure.Configuration;
using Shows.Infrastructure.Http;
using Shows.Infrastructure.Logging;
using Shows.Infrastructure.Persistence;

namespace Shows.App.Avalonia;

/// <summary>
/// The Avalonia entry point and composition root — the Avalonia analogue of the MAUI head's
/// <c>MauiProgram</c>. The only place the UI touches Infrastructure (<c>ARCHITECTURE.md</c> §2): it
/// builds the container, then hands it to <see cref="App"/> so the window and its ViewModels resolve
/// their services from it. No View or ViewModel names an Infrastructure type — only this file and
/// <c>App.axaml.cs</c> do.
/// </summary>
internal static class Program
{
    /// <summary>The process-wide container. Set in <see cref="Main"/> before the UI starts.</summary>
    public static IServiceProvider Services { get; private set; } = null!;

    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        Services = BuildServices();

        // Capture any crash in the log before the process dies. A background-thread exception (e.g. a
        // bound-property write made off the UI thread) terminates the process with no handler and, because
        // it unwinds past the code that would have logged it, leaves nothing in the file — the user sees a
        // silent crash. These handlers run first and write through the configured sink (the file sink is
        // not buffered), so the exception is on disk before termination. They do not swallow it: the
        // process still exits, but the next reader can see what happened.
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            CrashLog.LogError(e.ExceptionObject as Exception, "Unhandled exception terminated the app.");
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            CrashLog.LogError(e.Exception, "Unobserved task exception.");
            e.SetObserved();
        };

        // Bring the app's own directories and schema up before any window renders — the same startup
        // sequence the MAUI head runs (step 1.6 / ADR-028). DataPaths needs no database; the migrator is
        // the sanctioned migrate path and refuses under a live job lease rather than racing a writer.
        InitializeStorage(Services);

        // The options the composition root hands to View-layer helpers, read once here so no View or
        // ViewModel names an Infrastructure type (§2).
        var showsOptions = Services.GetRequiredService<
            Microsoft.Extensions.Options.IOptions<ShowsOptions>>().Value;

        // Hand the View-layer poster loader its disk-cache directory (improvement 17) and its proxy route
        // (SPEC R14). The composition root is the only place that names DataPaths or builds the proxy, so
        // the View stays free of Infrastructure (§2). ProxyConfig turns Proxy:Images into an IWebProxy (a
        // BCL type) — empty means direct, exactly as the named API clients do.
        var imageProxy = ProxyConfig.CreateProxy(showsOptions.Proxy.Images);
        ImageBehavior.Configure(Services.GetRequiredService<DataPaths>().PosterCacheDirectory, imageProxy);

        // Hand the View-layer link builder its URL formats (improvement 20). Same reason: the composition
        // root reads ShowsOptions.Links so no View or ViewModel names an Infrastructure type (§2).
        ShowLinks.Configure(showsOptions.Links.Tvmaze, showsOptions.Links.Imdb);

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    /// <summary>
    /// Build the DI container. Mirrors the MAUI head's wiring: logging first (console off — the app has
    /// no console), then Infrastructure (context, repositories, mapper, id generator, lease, use cases),
    /// then the Avalonia-specific singletons (navigation, preferences, browser) and the ViewModels.
    /// </summary>
    private static ServiceProvider BuildServices()
    {
        var services = new ServiceCollection();

        // appsettings.json flows transitively from Infrastructure as Content (ARCHITECTURE.md §4), so the
        // app gets a copy without declaring it. Read from the assembly directory (not the working
        // directory) via an explicit PhysicalFileProvider — the same content-root lesson the jobs hit,
        // where a bare path resolves against the host's ContentRootPath and is silently not found.
        // Optional so a missing file degrades to defaults. reloadOnChange is false, so the provider is
        // only consulted during Build and can be disposed immediately after (CA2000).
        IConfiguration configuration;
        using (var provider = new PhysicalFileProvider(AppContext.BaseDirectory))
        {
            configuration = new ConfigurationBuilder()
                .AddJsonFile(provider, "appsettings.json", optional: true, reloadOnChange: false)
                .Build();
        }

        // Logging first, before the rest of the container (ARCHITECTURE.md §7). Console off — the app
        // path is the safe default of AddShowsLogging.
        services.AddShowsLogging(typeof(Program).Assembly);
        services.AddShowsInfrastructure(configuration);

        // The Avalonia navigation seam (the MAUI Shell equivalent). Singleton — it holds no per-request
        // state. Preferences and Browser are static shims (no registration needed).
        services.AddSingleton<NavigationService>();

        // ViewModels. Transient: each tab/page gets its own instance, and the ViewModels hold only the
        // singleton IServiceScopeFactory (never a scoped service), so they are safe to live as long as
        // the window. The composition root is the only place the UI touches the container.
        services.AddTransient<ShellViewModel>();
        services.AddTransient<TrackingViewModel>();
        services.AddTransient<SearchViewModel>();
        services.AddTransient<DiscoveryViewModel>();
        services.AddTransient<ManageViewModel>();
        services.AddTransient<ShowDetailsViewModel>();
        services.AddTransient<SearchDetailsViewModel>();

        return services.BuildServiceProvider();
    }

    private static void InitializeStorage(IServiceProvider services)
    {
        var paths = services.GetRequiredService<DataPaths>();
        paths.EnsureDirectories();

        // Migrate on startup, gated on the lease rule (step 1.1). A live lease means a job is mid-write;
        // the migrator throws LiveLeaseException naming the job, which surfaces here as a clear message
        // rather than a schema change under a running job.
        var migrator = services.GetRequiredService<ShowsDatabaseMigrator>();
        migrator.MigrateAsync().GetAwaiter().GetResult();

        // Opportunistic http_cache housekeeping: drop validator-less rows past their reuse window, which
        // can never be served again. Runs after migration (the table exists, no lease is live). Best-effort
        // — a failure here must not stop the app from starting, so it is logged and swallowed rather than
        // thrown (a stale cache row costs disk, nothing more).
        try
        {
            services.GetRequiredService<HttpCacheMaintenance>()
                .PurgeExpiredValidatorlessAsync()
                .GetAwaiter()
                .GetResult();
        }
        catch (Exception ex)
        {
            CrashLog.LogWarning(ex, "http_cache startup purge failed; continuing.");
        }
    }

    // Avalonia configuration, don't remove; also used by the visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
