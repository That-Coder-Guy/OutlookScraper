using System.Runtime.Versioning;
using System.Windows.Threading;

namespace OutlookScraper.Outlook;

/// <summary>
/// A dedicated single-threaded-apartment thread running a real message pump, on which
/// all Outlook COM work happens.
/// </summary>
/// <remarks>
/// Two things make this necessary rather than merely tidy:
///
/// COM events are delivered as window messages to the apartment that created the
/// proxy. Without a running pump the subscriptions appear to succeed and then simply
/// never fire — no error, no exception, just silence.
///
/// And it is deliberately *not* the WPF UI thread. Reading <c>MailItem.Body</c> on a
/// cold item can block for a noticeable time, and that must never freeze the window.
/// It also means Outlook connectivity is independent of whether any window is open.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class OutlookStaThread : IAsyncDisposable
{
    private readonly Thread _thread;
    private readonly TaskCompletionSource _ready =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private Dispatcher? _dispatcher;

    public OutlookStaThread()
    {
        _thread = new Thread(Pump)
        {
            Name = "Outlook-STA",

            // Foreground on purpose: a background thread would be torn down at process
            // exit, potentially mid-COM-call. Shutdown is explicit instead.
            IsBackground = false,
        };

        // Must be set before Start; afterwards it throws.
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    private void Pump()
    {
        _dispatcher = Dispatcher.CurrentDispatcher;
        _ready.SetResult();

        // Blocks until InvokeShutdown. This is the pump COM events arrive on.
        Dispatcher.Run();
    }

    public Task ReadyAsync() => _ready.Task;

    public async Task<T> InvokeAsync<T>(Func<T> work)
    {
        await _ready.Task;
        return await _dispatcher!.InvokeAsync(work).Task;
    }

    public async Task InvokeAsync(Action work)
    {
        await _ready.Task;
        await _dispatcher!.InvokeAsync(work).Task;
    }

    /// <summary>Fire-and-forget, for COM event handlers that must return immediately.</summary>
    public void Post(Action work) => _dispatcher?.BeginInvoke(work);

    public async ValueTask DisposeAsync()
    {
        if (_dispatcher is null)
        {
            return;
        }

        _dispatcher.InvokeShutdown();

        await Task.Run(() =>
        {
            if (!_thread.Join(TimeSpan.FromSeconds(5)))
            {
                // The pump is wedged inside a COM call. Nothing safe left to do; the
                // process is exiting anyway.
            }
        });
    }
}
