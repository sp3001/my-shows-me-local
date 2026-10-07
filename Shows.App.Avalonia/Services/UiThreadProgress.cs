using Avalonia.Threading;

namespace Shows.App.Avalonia.Services;

/// <summary>
/// An <see cref="IProgress{T}"/> that delivers every report on the Avalonia UI thread. A job reports
/// progress on its own worker thread; the Manage page binds its progress properties, and a bound write
/// off the UI thread crashes the process (the recurring Avalonia "Call from invalid thread" trap). Wrapping
/// the sink in this type means the callback the job invokes is safe to call from any thread — it posts the
/// real handler onto the dispatcher.
/// </summary>
/// <remarks>
/// Uses <see cref="Dispatcher.UIThread"/> directly rather than capturing a
/// <see cref="SynchronizationContext"/>, so delivery is pinned to the UI thread regardless of the context
/// (or lack of one) the reporter was constructed under. This is the same mechanism
/// <see cref="MainThread.BeginInvokeOnMainThread"/> wraps, expressed once for the progress-report shape.
/// </remarks>
internal sealed class UiThreadProgress<T>(Action<T> handler) : IProgress<T>
{
    public void Report(T value) => Dispatcher.UIThread.Post(() => handler(value));
}
