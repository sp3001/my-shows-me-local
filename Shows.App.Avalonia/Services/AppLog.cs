using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Shows.App.Avalonia.Services;

/// <summary>
/// Resolves an <see cref="ILogger{T}"/> from the composition-root container for the few View-layer
/// helpers that genuinely need to log (poster loading, browser launch, preference persistence). These
/// helpers cannot take a constructor-injected logger — they are static shims — so they reach the
/// container lazily through <see cref="Program.Services"/>.
/// </summary>
/// <remarks>
/// Only the <c>Microsoft.Extensions.Logging</c> abstraction is named here, never an Infrastructure type,
/// so the layering rule (no View names Infrastructure — <c>ARCHITECTURE.md</c> §2) holds: the concrete
/// Serilog sink is wired once in the composition root by <c>AddShowsLogging</c>. Resolution is lazy
/// because a static helper's first use is always after <see cref="Program.Main"/> has set the container.
/// </remarks>
internal static class AppLog
{
    public static ILogger<T> For<T>() => Program.Services.GetRequiredService<ILogger<T>>();
}

/// <summary>
/// Logs a crash from the composition root's global exception handlers, before the process terminates.
/// Uses a fixed source context (there is no generic type argument to key a logger on at that point) and
/// writes at Error so the entry survives regardless of the configured minimum level. The file sink is not
/// buffered, so the line is on disk before the runtime tears the process down.
/// </summary>
internal static class CrashLog
{
    public static void LogError(Exception? exception, string message)
    {
        try
        {
            Program.Services.GetRequiredService<ILoggerFactory>()
                .CreateLogger("Shows.App.Crash")
                .LogError(exception, "{Message}", message);
        }
        catch
        {
            // Logging a crash must never itself throw and mask the original failure.
        }
    }

    /// <summary>
    /// Logs a non-fatal startup warning (e.g. a best-effort cleanup that failed but must not stop the app).
    /// Same fixed source context and never-throw guarantee as <see cref="LogError"/>, at Warning level so a
    /// graceful-degradation failure is distinguishable from a crash.
    /// </summary>
    public static void LogWarning(Exception? exception, string message)
    {
        try
        {
            Program.Services.GetRequiredService<ILoggerFactory>()
                .CreateLogger("Shows.App.Crash")
                .LogWarning(exception, "{Message}", message);
        }
        catch
        {
            // Logging must never itself throw.
        }
    }
}
