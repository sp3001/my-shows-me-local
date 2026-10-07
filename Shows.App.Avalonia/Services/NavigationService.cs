using System;
using System.Globalization;
using System.Threading.Tasks;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Shows.App.Avalonia.ViewModels;

namespace Shows.App.Avalonia.Services;

/// <summary>
/// The Avalonia replacement for Shell's <c>GoToAsync</c> routing. The ViewModels call
/// <c>Shell.Current.GoToAsync(route)</c> with the MAUI route strings; this parses them and pushes/pops
/// the corresponding page on the <see cref="ShellViewModel"/>'s navigation stack. Registered as a
/// singleton and installed as <see cref="Shell.Current"/> when the shell is constructed.
/// </summary>
/// <remarks>
/// Routes recognised (identical to the MAUI app): <c>ShowDetails?id={long}</c>,
/// <c>SearchDetails?tvmazeId={int}</c>, and <c>..</c> (pop back). A GoToAsync can complete on a
/// background thread (the callers use <c>ConfigureAwait(false)</c>), so the stack mutation is marshalled
/// to the UI thread.
/// </remarks>
public sealed class NavigationService : IShellNavigation
{
    private readonly IServiceProvider _services;
    private ShellViewModel? _shell;

    public NavigationService(IServiceProvider services)
    {
        _services = services;
    }

    /// <summary>Wire the shell once it exists and take over <see cref="Shell.Current"/>.</summary>
    public void Attach(ShellViewModel shell)
    {
        _shell = shell;
        Shell.Current = this;
    }

    public Task GoToAsync(string route)
    {
        // The whole navigation is a UI-thread operation (it mutates the bound stack). Post and return so
        // the caller's await does not deadlock waiting on a dispatcher it is currently blocking.
        Dispatcher.UIThread.Post(() => NavigateCore(route));
        return Task.CompletedTask;
    }

    private void NavigateCore(string route)
    {
        if (_shell is null)
        {
            return;
        }

        // Pop back to the tabs.
        if (route.Trim() == "..")
        {
            _shell.Pop();
            return;
        }

        var (path, query) = SplitRoute(route);

        if (path == "ShowDetails" && TryGetLong(query, "id", out var trackedId))
        {
            var vm = _services.GetRequiredService<ShowDetailsViewModel>();
            _shell.Push(vm);
            _ = vm.LoadAsync(trackedId);
            return;
        }

        if (path == "SearchDetails" && TryGetInt(query, "tvmazeId", out var tvmazeId))
        {
            var vm = _services.GetRequiredService<SearchDetailsViewModel>();
            _shell.Push(vm);
            _ = vm.LoadAsync(tvmazeId);
        }
    }

    // "ShowDetails?id=123" -> ("ShowDetails", { id: "123" }). Mirrors Shell's query-string routing.
    private static (string Path, System.Collections.Generic.IDictionary<string, string> Query) SplitRoute(string route)
    {
        var trimmed = route.TrimStart('/');
        var split = trimmed.Split('?', 2);
        var query = new System.Collections.Generic.Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (split.Length > 1)
        {
            foreach (var pair in split[1].Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var kv = pair.Split('=', 2);
                if (kv.Length == 2)
                {
                    query[kv[0]] = Uri.UnescapeDataString(kv[1]);
                }
            }
        }

        return (split[0], query);
    }

    private static bool TryGetLong(System.Collections.Generic.IDictionary<string, string> query, string key, out long value)
    {
        value = 0;
        return query.TryGetValue(key, out var raw)
               && long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    private static bool TryGetInt(System.Collections.Generic.IDictionary<string, string> query, string key, out int value)
    {
        value = 0;
        return query.TryGetValue(key, out var raw)
               && int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }
}
