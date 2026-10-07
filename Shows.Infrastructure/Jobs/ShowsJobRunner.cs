using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;
using Shows.Infrastructure.Configuration;
using Shows.Infrastructure.Logging;

namespace Shows.Infrastructure.Jobs;

/// <summary>
/// Shared shell for the console jobs (ARCHITECTURE.md §3, IMPLEMENTATION_PLAN.md step 0.7). Each
/// <c>Program.cs</c> is a one-line call here — no business logic lives in <c>Program.cs</c>. The shell
/// builds a host, configures logging first (ARCHITECTURE.md §7), resolves configuration, logs its
/// identity and resolved paths, and exits 0.
/// </summary>
/// <remarks>
/// The real <c>job_run</c> row is written once the table exists (step 1.2). Until then the intent is
/// logged at <c>Information</c> so a manual run is observable and the exit-0 contract is testable now.
/// </remarks>
public static class ShowsJobRunner
{
    /// <summary>
    /// Build the host, configure logging and configuration, then either run <paramref name="handler"/>
    /// (a job that does real work, given the root provider and the raw args) or, when none is supplied,
    /// log the shell-complete line and exit 0. The five not-yet-implemented jobs pass no handler and so
    /// keep their step-0.7 behavior unchanged.
    /// </summary>
    public static async Task<int> RunAsync(
        string[] args,
        Assembly callingAssembly,
        Func<IServiceProvider, string[], Task<int>>? handler = null)
    {
        var jobName = callingAssembly.GetName().Name ?? "job";

        try
        {
            var builder = Host.CreateApplicationBuilder(args);

            // appsettings.json sits beside the executable's output (it flows transitively from
            // Infrastructure as Content, ARCHITECTURE.md §7), NOT the working directory — `dotnet run
            // --project` sets the working directory to the project folder, where there is no
            // appsettings.json, so the default builder source finds nothing and every Shows:* value is
            // empty. Load it from the assembly directory, the same place AddShowsLogging reads
            // logging.json from, so config resolves identically under `dotnet run` and a published exe.
            //
            // An explicit PhysicalFileProvider rooted at the assembly directory is required, not just an
            // absolute Path: a JsonConfigurationSource with no FileProvider resolves its Path against the
            // host's ContentRootPath (the project folder under `dotnet run`), and a path outside that root
            // is not found — which silently leaves every Shows:* value empty and the named clients with no
            // BaseAddress. Pinning the provider to the assembly directory removes the ambiguity.
            //
            // Insert at the FRONT, not AddJsonFile (which appends): the default builder already added
            // env vars and command-line args, and a source added after them would OVERRIDE them —
            // backwards for a container, where env vars are how you set BaseDirectory/proxy at deploy
            // time (ARCHITECTURE.md §4). At the front, the file is the base and env/args still win.
            var assemblyDirectory = Path.GetDirectoryName(callingAssembly.Location)!;
            ((IConfigurationBuilder)builder.Configuration).Sources.Insert(0, new JsonConfigurationSource
            {
                Path = "appsettings.json",
                Optional = true,
                ReloadOnChange = false,
                FileProvider = new PhysicalFileProvider(assemblyDirectory),
            });

            // Logging first, before the rest of the container (ARCHITECTURE.md §7). Console is on: this
            // is a job, so a manual run should be readable live.
            builder.Services.AddShowsLogging(callingAssembly, enableConsole: true);
            builder.Services.AddShowsInfrastructure(builder.Configuration);

            using var host = builder.Build();

            // Resolve a real service rather than a sentinel — proves the container is wired, not just
            // that the process started. DataPaths needs no database, so it is safe before step 1.1.
            var paths = host.Services.GetRequiredService<DataPaths>();
            paths.EnsureDirectories();

            var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger(jobName);

            logger.LogInformation(
                "{Job} starting. Base={Base} Database={Database} ImdbDataset={Imdb} Log={Log}",
                jobName, paths.BaseDirectory, paths.DatabasePath, paths.ImdbDatasetPath, paths.LogDirectory);

            // job_run row arrives with the table (step 1.2). Until then, log the intent — SPEC/SCHEMA §2
            // mirror this row, so the summary line is the temporary audit trail.
            logger.LogInformation(
                "job_run intent: {Job} started {Started:O} (job_run table pending step 1.2)",
                jobName, DateTimeOffset.UtcNow);

            if (handler is not null)
            {
                return await handler(host.Services, args);
            }

            logger.LogInformation("{Job} completed with no work (shell only, step 0.7).", jobName);

            return 0;
        }
        finally
        {
            // ARCHITECTURE.md §7 / step 0.8: the tail line must reach the file even on the exception
            // path. CloseAndFlush is idempotent and safe to call on the happy path too.
            await Log.CloseAndFlushAsync();
        }
    }
}
