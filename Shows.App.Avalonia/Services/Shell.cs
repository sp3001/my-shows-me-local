using System;
using System.Threading.Tasks;

namespace Shows.App.Avalonia;

/// <summary>
/// Avalonia stand-in for <c>Microsoft.Maui.Controls.Shell</c>. The ViewModels navigate by calling
/// <c>Shell.Current.GoToAsync(route)</c> with the same route strings the MAUI app uses
/// (<c>ShowDetails?id=…</c>, <c>SearchDetails?tvmazeId=…</c>, <c>..</c>). Avalonia has no Shell, so this
/// static seam forwards those calls to the app's <see cref="Services.NavigationService"/>, which the
/// composition root installs as <see cref="Current"/>. Declared in the app's root namespace and pulled
/// in by a global using so the copied ViewModels compile unchanged.
/// </summary>
public static class Shell
{
    /// <summary>
    /// The active navigation seam. Defaults to a no-op so a stray call before the window is up (or in a
    /// headless/design context) is inert rather than a null reference; <c>NavigationService</c> replaces
    /// it when the shell is constructed.
    /// </summary>
    public static IShellNavigation Current { get; set; } = NullShellNavigation.Instance;
}

/// <summary>The contract <see cref="Shell.Current"/> forwards to. Mirrors the slice of Shell the VMs use.</summary>
public interface IShellNavigation
{
    Task GoToAsync(string route);
}

internal sealed class NullShellNavigation : IShellNavigation
{
    public static readonly NullShellNavigation Instance = new NullShellNavigation();

    public Task GoToAsync(string route) => Task.CompletedTask;
}
