using System;
using Avalonia.Threading;

namespace Shows.App.Avalonia;

/// <summary>
/// Avalonia stand-in for <c>Microsoft.Maui.Controls.MainThread</c>. The ViewModels marshal a
/// collection mutation or an observable write back to the UI thread after an off-UI
/// (<c>ConfigureAwait(false)</c>) continuation; the MAUI call maps directly onto Avalonia's dispatcher
/// (the migration guide's <c>MainThread.BeginInvokeOnMainThread ? Dispatcher.UIThread.Post</c>).
/// Declared in the app's root namespace and pulled in by a global using so the copied ViewModels
/// compile unchanged.
/// </summary>
public static class MainThread
{
    public static void BeginInvokeOnMainThread(Action action) =>
        Dispatcher.UIThread.Post(action);
}
