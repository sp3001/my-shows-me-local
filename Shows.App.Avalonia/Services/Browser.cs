using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Shows.App.Avalonia.Services;

namespace Shows.App.Avalonia;

/// <summary>
/// Avalonia stand-in for <c>Microsoft.Maui.Controls.Browser</c>. Opens a URL in the user's default
/// browser via the OS shell — the details page's TVmaze / IMDb links (SPEC P5: the URL is built from
/// the id, never stored). Fire-and-forget on non-Windows; on Windows <c>UseShellExecute</c> hands the
/// URI to the registered handler.
/// </summary>
public sealed class Browser
{
    private Browser()
    {
    }

    public static Task OpenAsync(Uri uri)
    {
        var psi = new ProcessStartInfo
        {
            FileName = uri.AbsoluteUri,
            UseShellExecute = true,
        };

        try
        {
            Process.Start(psi);
        }
        catch (Exception ex)
        {
            // Opening a browser is a convenience; a failure here must not crash the details page. Logged
            // as a warning (the page still works, only the external link did not open) rather than
            // swallowed, so a broken shell handler is visible in the log.
            Log.LogWarning(ex, "Browser.OpenAsync failed for {Uri}", uri);
        }

        return Task.CompletedTask;
    }

    private static ILogger Log => _log ??= AppLog.For<Browser>();

    private static ILogger? _log;
}
