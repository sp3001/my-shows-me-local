using Microsoft.Extensions.Options;

namespace Shows.Infrastructure.Configuration;

/// <summary>
/// Resolves the app's own paths against one base directory, so the whole app is relocatable and the
/// Docker migration is a volume mount rather than a code change (<c>ARCHITECTURE.md</c> §4).
/// </summary>
/// <remarks>
/// <para>
/// Built once at startup, from options. Every consumer takes this rather than composing paths itself,
/// which is what keeps <c>data/</c> and <c>log/</c> beside each other under one base directory — the
/// property that makes "back this one directory up" the correct backup instruction (SPEC P4).
/// </para>
/// <para>
/// Relative configured paths are resolved against <see cref="BaseDirectory"/>; absolute ones are
/// honoured as given, so a container can point the database at a mount without a code change.
/// </para>
/// </remarks>
public sealed class DataPaths
{
    private const string SolutionMarker = "Shows.sln";

    private readonly DataOptions _data;
    private readonly DiscoveryOptions _discovery;

    public DataPaths(IOptions<ShowsOptions> options)
    {
        _data = options.Value.Data;
        _discovery = options.Value.Discovery;
        BaseDirectory = ResolveBaseDirectory(_data.BaseDirectory);
    }

    public string BaseDirectory { get; }

    public string DatabasePath => Resolve(_data.DatabasePath, "data/shows.db");

    public string ImdbDatasetPath => Resolve(_data.ImdbDatasetPath, "data/imdb");

    /// <summary>
    /// Where <c>--report</c> writes the <c>v_staging_issues</c> CSV (step 2.4). Resolved like every other
    /// data path so the report lands in the data folder beside the database, and a container can point it
    /// at a mount without a code change.
    /// </summary>
    public string ReportPath => Resolve(_data.ReportPath, "data/staging_issues.csv");

    /// <summary>
    /// Where the read-only myshows exports (<c>watching.json</c> etc.) live. Defaults to the base
    /// directory, which is the repo root in development — the inputs sit beside <c>Shows.sln</c>
    /// (AGENTS.md). Resolved like the others so a container can point it at a mount without a code change.
    /// </summary>
    public string InputDirectory => Resolve(_data.InputDirectory, ".");

    /// <summary>
    /// Beside <c>data/</c>, not under it (<c>ARCHITECTURE.md</c> §7). Logs are not data and must not be
    /// swept into a database backup, but they share the base directory so the app stays relocatable.
    /// </summary>
    public string LogDirectory => Path.Combine(BaseDirectory, "log");

    /// <summary>
    /// Where the UI caches downloaded poster images (improvement 17). Beside <c>data/</c> and <c>log/</c>,
    /// not under <c>data/</c>: posters are rebuildable derived data (re-fetchable from TVmaze), not the
    /// user's irreplaceable watch history (SPEC P4), so they must not be swept into a database backup.
    /// Sharing the base directory keeps the app relocatable.
    /// </summary>
    public string PosterCacheDirectory => Path.Combine(BaseDirectory, "cache", "posters");

    /// <summary>
    /// The saved <c>schedule-full.json</c> the Discovery source parses when
    /// <c>DiscoveryOptions.ScheduleSourcePath</c> is set (user-issues 19). Resolved like every other data
    /// path so a relative value lands in the repo root (where the user's sample sits) and a container can
    /// point it at a mount without a code change. Empty when unset (the source then fetches live).
    /// </summary>
    public string DiscoverySchedulePath =>
        string.IsNullOrWhiteSpace(_discovery.ScheduleSourcePath)
            ? string.Empty
            : Path.IsPathRooted(_discovery.ScheduleSourcePath)
                ? _discovery.ScheduleSourcePath
                : Path.GetFullPath(Path.Combine(BaseDirectory, _discovery.ScheduleSourcePath));

    private string Resolve(string relative, string fallback)
    {
        var value = string.IsNullOrWhiteSpace(relative) ? fallback : relative;

        return Path.IsPathRooted(value)
            ? value
            : Path.GetFullPath(Path.Combine(BaseDirectory, value));
    }

    /// <summary>
    /// Shared by <see cref="DataPaths"/> and the logging setup (ARCHITECTURE.md §7): logging runs
    /// before options are available, so it resolves the same base directory through this one rule
    /// rather than a second copy of the marker walk.
    /// </summary>
    public static string ResolveBaseDirectory(string configured)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return Path.GetFullPath(configured);
        }

        // Development discovery: walk up from the assembly directory to the repo root, identified by
        // the solution file. Without this, `dotnet run` puts the user's database under bin/Debug and
        // every "back up data/" instruction in these docs becomes false.
        var probe = new DirectoryInfo(AppContext.BaseDirectory);
        while (probe is not null)
        {
            if (File.Exists(Path.Combine(probe.FullName, SolutionMarker)))
            {
                return probe.FullName;
            }

            probe = probe.Parent;
        }

        // Nothing to walk up to — a container or an installed app. Fall back to the assembly
        // directory, where a configured BaseDirectory or a volume mount is expected.
        return AppContext.BaseDirectory;
    }

    /// <summary>Creates the directories this instance points at. Called once at startup.</summary>
    public void EnsureDirectories()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath)
            ?? throw new InvalidOperationException($"'{DatabasePath}' has no parent directory."));
        Directory.CreateDirectory(ImdbDatasetPath);
        Directory.CreateDirectory(LogDirectory);
        Directory.CreateDirectory(PosterCacheDirectory);
    }
}
