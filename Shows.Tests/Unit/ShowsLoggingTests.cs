using Microsoft.Extensions.Configuration;
using Serilog;
using Serilog.Events;
using Shows.Infrastructure.Logging;
using Xunit;

namespace Shows.Tests.Unit;

/// <summary>
/// Step 0.8 acceptance: "a level filter nobody has seen fail is not a level filter" and
/// "Log.CloseAndFlush() runs on the exception path". Both are exercised against the real sink
/// configuration via the internal CreateLogger seam, writing to a throwaway file.
/// </summary>
public sealed class ShowsLoggingTests : IDisposable
{
    private readonly string _logPath;

    public ShowsLoggingTests()
    {
        _logPath = Path.Combine(Path.GetTempPath(), $"shows-log-test-{Guid.NewGuid():N}.log");
    }

    [Fact]
    public void DebugLevel_WritesDebugLine()
    {
        var logger = ShowsLoggingServiceCollectionExtensions.CreateLogger(
            _logPath, LogEventLevel.Debug, enableConsole: false);

        logger.Debug("sentinel-debug-line");
        Dispose(logger);

        Assert.Contains("sentinel-debug-line", File.ReadAllText(_logPath));
    }

    [Fact]
    public void InformationLevel_OmitsDebugLine()
    {
        var logger = ShowsLoggingServiceCollectionExtensions.CreateLogger(
            _logPath, LogEventLevel.Information, enableConsole: false);

        logger.Debug("sentinel-debug-line");
        logger.Information("sentinel-info-line");
        Dispose(logger);

        var text = File.ReadAllText(_logPath);
        Assert.DoesNotContain("sentinel-debug-line", text);
        Assert.Contains("sentinel-info-line", text);
    }

    [Fact]
    public void LogLevelEnvironmentVariable_OverridesDefault()
    {
        var previous = Environment.GetEnvironmentVariable("LOG_LEVEL");
        try
        {
            // Empty config: no Serilog:MinimumLevel:Default, so only LOG_LEVEL can resolve a level.
            var emptyConfig = new ConfigurationBuilder().Build();

            Environment.SetEnvironmentVariable("LOG_LEVEL", "Debug");
            Assert.Equal(LogEventLevel.Debug, ShowsLoggingServiceCollectionExtensions.ResolveMinimumLevel(emptyConfig));

            Environment.SetEnvironmentVariable("LOG_LEVEL", "Error");
            Assert.Equal(LogEventLevel.Error, ShowsLoggingServiceCollectionExtensions.ResolveMinimumLevel(emptyConfig));
        }
        finally
        {
            Environment.SetEnvironmentVariable("LOG_LEVEL", previous);
        }
    }

    [Fact]
    public void ConfigDefault_IsUsedWhenNoEnvironmentOverride()
    {
        var previous = Environment.GetEnvironmentVariable("LOG_LEVEL");
        try
        {
            Environment.SetEnvironmentVariable("LOG_LEVEL", null);

            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Serilog:MinimumLevel:Default"] = "Debug",
                })
                .Build();

            Assert.Equal(LogEventLevel.Debug, ShowsLoggingServiceCollectionExtensions.ResolveMinimumLevel(config));
        }
        finally
        {
            Environment.SetEnvironmentVariable("LOG_LEVEL", previous);
        }
    }

    [Fact]
    public void MissingConfigDefault_FailsClosedToWarning()
    {
        var previous = Environment.GetEnvironmentVariable("LOG_LEVEL");
        try
        {
            Environment.SetEnvironmentVariable("LOG_LEVEL", null);
            var emptyConfig = new ConfigurationBuilder().Build();

            Assert.Equal(LogEventLevel.Warning, ShowsLoggingServiceCollectionExtensions.ResolveMinimumLevel(emptyConfig));
        }
        finally
        {
            Environment.SetEnvironmentVariable("LOG_LEVEL", previous);
        }
    }

    [Fact]
    public void CloseAndFlush_PersistsTailLineOnExceptionPath()
    {
        // Reproduces the ShowsJobRunner try/finally contract (ARCHITECTURE.md §7): a line logged just
        // before an exception must still reach the file, because CloseAndFlush runs in the finally.
        var logger = ShowsLoggingServiceCollectionExtensions.CreateLogger(
            _logPath, LogEventLevel.Information, enableConsole: false);

        var threw = false;
        try
        {
            logger.Information("tail-line-before-throw");
            throw new InvalidOperationException("deliberate failure");
        }
        catch (InvalidOperationException)
        {
            threw = true;
        }
        finally
        {
            ((IDisposable)logger).Dispose(); // Serilog flushes sinks on dispose — the CloseAndFlush equivalent.
        }

        Assert.True(threw);
        Assert.Contains("tail-line-before-throw", File.ReadAllText(_logPath));
    }

    public void Dispose()
    {
        if (File.Exists(_logPath))
        {
            File.Delete(_logPath);
        }
    }

    private static void Dispose(ILogger logger) => ((IDisposable)logger).Dispose();
}
