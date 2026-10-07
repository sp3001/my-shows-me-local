using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using Serilog.Events;
using Shows.Infrastructure.Configuration;

namespace Shows.Infrastructure.Logging;

/// <summary>
/// The single logging entry point for every executable (ARCHITECTURE.md §7). Called first in every
/// <c>Program.cs</c>/<c>MauiProgram.cs</c>, before <c>AddShowsInfrastructure()</c>. No project gets to
/// invent its own sinks, levels, or format — adding a seventh job is the same two lines.
/// </summary>
public static class ShowsLoggingServiceCollectionExtensions
{
    // ARCHITECTURE.md §7 "Format": one line per event, exceptions appended. Kept here rather than in
    // logging.json because the file sink's path is dynamic (per-app-name) and configured in code, so
    // the template lives with the sink that uses it.
    private const string OutputTemplate =
        "[{Timestamp:HH:mm:ss.fff}] [{Level:u3}] [{SourceContext}] {Message:lj} {Exception}{NewLine}";

    private const long FileSizeLimitBytes = 10 * 1024 * 1024;
    private const int RetainedFileCountLimit = 10;

    /// <param name="callingAssembly">
    /// The executable's own assembly. <c>&lt;appname&gt;</c> in <c>log/&lt;appname&gt;.log</c> comes from
    /// <see cref="Assembly.GetName"/> — never hardcoded (ARCHITECTURE.md §7).
    /// </param>
    /// <param name="enableConsole">
    /// Console sink is jobs-only (ARCHITECTURE.md §7): a manual run should be readable live, while the
    /// MAUI app has no console and must not attempt one. Defaults to <c>false</c> so the app path is the
    /// safe default and each job opts in explicitly.
    /// </param>
    public static IServiceCollection AddShowsLogging(
        this IServiceCollection services, Assembly callingAssembly, bool enableConsole = false)
    {
        var configuration = BuildLoggingConfiguration();
        var logPath = ResolveLogPath(configuration, callingAssembly);

        Log.Logger = CreateLogger(logPath, ResolveMinimumLevel(configuration), enableConsole);

        services.AddSerilog(Log.Logger, dispose: true);

        return services;
    }

    // Split out so the level filter can be exercised against a temp file in a test (step 0.8
    // acceptance: "a level filter nobody has seen fail is not a level filter"). The public path always
    // routes through here, so the test exercises the same sink configuration production uses.
    internal static ILogger CreateLogger(string logPath, LogEventLevel minimumLevel, bool enableConsole)
    {
        var configuration = BuildLoggingConfiguration();

        var builder = new LoggerConfiguration()
            // Per-namespace overrides and the default level are owned by logging.json — the single
            // source of truth. MinimumLevel.Is is applied after ReadFrom.Configuration so the resolved
            // value (a LOG_LEVEL override, else the config default) wins over the file's Default.
            .ReadFrom.Configuration(configuration)
            .MinimumLevel.Is(minimumLevel)
            .WriteTo.File(
                logPath,
                LogEventLevel.Verbose,
                outputTemplate: OutputTemplate,
                // Size-only rolling (ARCHITECTURE.md §7): Infinite = no date component, so rolls append
                // a counter (appname.log, appname_001.log) instead of fragmenting by day.
                rollingInterval: RollingInterval.Infinite,
                rollOnFileSizeLimit: true,
                fileSizeLimitBytes: FileSizeLimitBytes,
                retainedFileCountLimit: RetainedFileCountLimit);

        if (enableConsole)
        {
            builder.WriteTo.Console(outputTemplate: OutputTemplate);
        }

        return builder.CreateLogger();
    }

    // logging.json sits beside this assembly's output (copied by the csproj). Read it directly rather
    // than through the host's IConfiguration: logging is configured before the host is built, and the
    // level overrides must not depend on appsettings being parsed first.
    private static IConfiguration BuildLoggingConfiguration() =>
        new ConfigurationBuilder()
            .SetBasePath(Path.GetDirectoryName(typeof(ShowsLoggingServiceCollectionExtensions)
                .Assembly.Location)!)
            .AddJsonFile("logging.json", optional: false)
            .Build();

    // Same base-directory rule as DataPaths (§4) so log/ and data/ stay siblings under one root — the
    // property that keeps "back up this one directory" true. <appname> comes from the calling
    // assembly's simple name, never hardcoded (ARCHITECTURE.md §7).
    private static string ResolveLogPath(IConfiguration configuration, Assembly callingAssembly)
    {
        var configured = configuration["Shows:Data:BaseDirectory"];
        var baseDirectory = DataPaths.ResolveBaseDirectory(configured ?? string.Empty);
        var logDirectory = Path.Combine(baseDirectory, "log");
        Directory.CreateDirectory(logDirectory);

        var appName = callingAssembly.GetName().Name
            ?? throw new InvalidOperationException("Calling assembly has no simple name for the log file.");

        return Path.Combine(logDirectory, $"{appName}.log");
    }

    // The default minimum level lives in logging.json (Serilog:MinimumLevel:Default) — the single
    // source of truth — not in a build-configuration branch. LOG_LEVEL still overrides it for ad-hoc
    // runs without editing the file. If the config value is missing or unparseable we fail closed to
    // Warning rather than silently flooding the file sink.
    internal static LogEventLevel ResolveMinimumLevel(IConfiguration configuration)
    {
        var fromEnvironment = Environment.GetEnvironmentVariable("LOG_LEVEL");
        if (!string.IsNullOrWhiteSpace(fromEnvironment)
            && Enum.TryParse<LogEventLevel>(fromEnvironment, ignoreCase: true, out var fromEnv))
        {
            return fromEnv;
        }

        var fromConfig = configuration["Serilog:MinimumLevel:Default"];
        if (!string.IsNullOrWhiteSpace(fromConfig)
            && Enum.TryParse<LogEventLevel>(fromConfig, ignoreCase: true, out var fromCfg))
        {
            return fromCfg;
        }

        return LogEventLevel.Warning;
    }
}
