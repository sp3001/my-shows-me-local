using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Shows.Application.UseCases;

namespace Shows.App.Avalonia.ViewModels;

/// <summary>
/// Debounced catalog search (SPEC F2). The ViewModel talks only to the Application use case
/// (<c>ARCHITECTURE.md</c> §3) and resolves it from a per-operation scope, never holding a scoped
/// service itself.
/// </summary>
/// <remarks>
/// <para>
/// <b>The debounce is the whole point of this class.</b> A naive per-keystroke search fires a TVmaze
/// request for every character, which both hammers a rate-limited host and makes the results flicker.
/// Each keystroke cancels the in-flight wait (via <see cref="_debounceCts"/>) and restarts it; only a
/// pause longer than <see cref="DebounceMs"/> lets a request through. The token is also handed to the
/// use case so a late response is discarded rather than racing a newer query's results onto the list.
/// </para>
/// </remarks>
public sealed partial class SearchViewModel : ObservableObject
{
    private const int DebounceMs = 400;

    private readonly IServiceScopeFactory _scopeFactory;
    private CancellationTokenSource? _debounceCts;

    public SearchViewModel(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory;
    }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SearchCommand))]
    private string _query = string.Empty;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string? _statusMessage;

    public ObservableCollection<ShowCardViewModel> Results { get; } = [];

    private bool CanSearch => !string.IsNullOrWhiteSpace(Query);

    [RelayCommand]
    private void Search() => _ = RunSearchAsync();

    /// <summary>
    /// Restart the debounce window on every keystroke, then search once the user pauses. Public so the
    /// page's <c>SearchBar</c> text-changed handler can drive it; the <see cref="SearchCommand"/> is the
    /// explicit submit (Enter key).
    /// </summary>
    public void OnQueryTextChanged(string text)
    {
        Query = text;
        if (string.IsNullOrWhiteSpace(text))
        {
            _ = CancelPendingAsync();
            Results.Clear();
            StatusMessage = null;
            return;
        }

        _ = RunSearchAsync();
    }

    private async Task CancelPendingAsync()
    {
        var previous = _debounceCts;
        _debounceCts = null;
        if (previous is not null)
        {
            // CancelAsync (CA1849) rather than the synchronously-blocking Cancel. The source is not
            // disposed here: a pending Task.Delay is still registered on its token, and disposing while
            // that continuation is scheduled would race. A plain CTS used only for Task.Delay holds no
            // unmanaged resource, so letting the superseded one be collected is correct.
            await previous.CancelAsync().ConfigureAwait(false);
        }
    }

    private async Task RunSearchAsync()
    {
        // Cancel any pending wait so only the latest keystroke's timer survives (CA1849: CancelAsync).
        var previous = _debounceCts;
        var cts = new CancellationTokenSource();
        _debounceCts = cts;
        if (previous is not null)
        {
            await previous.CancelAsync().ConfigureAwait(false);
        }

        try
        {
            // No ConfigureAwait(false) on either await below: this runs from a UI-thread command/handler,
            // and the code after each await writes bound properties (IsBusy / StatusMessage) and mutates
            // the bound Results collection. Resuming on the UI context keeps those on the UI thread;
            // resuming on the thread pool would raise PropertyChanged off-thread and crash the app.
            await Task.Delay(DebounceMs, cts.Token);
        }
        catch (OperationCanceledException)
        {
            return; // superseded by a newer keystroke
        }

        var token = cts.Token;
        IsBusy = true;
        StatusMessage = null;
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var search = scope.ServiceProvider.GetRequiredService<SearchShowsUseCase>();
            var found = await search.ExecuteAsync(Query, token);

            // A newer query started while we were fetching — drop these results.
            if (token.IsCancellationRequested)
            {
                return;
            }

            Results.Clear();
            foreach (var show in found)
            {
                Results.Add(new ShowCardViewModel(show, _scopeFactory));
            }

            StatusMessage = found.Count == 0 ? "No results." : null;
        }
        catch (OperationCanceledException)
        {
            // fine — superseded
        }
        catch (Exception ex)
        {
            // R14: a transport failure names the host. Surface it; never render an empty list as "no results".
            StatusMessage = ex.Message;
        }
        finally
        {
            if (!token.IsCancellationRequested)
            {
                IsBusy = false;
            }
        }
    }
}
