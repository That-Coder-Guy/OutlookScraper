using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace OutlookScraper.Outlook;

/// <summary>
/// A dedicated single-threaded-apartment thread running a Win32 message pump, on which
/// all Outlook COM work happens.
/// </summary>
/// <remarks>
/// Two things make this necessary rather than merely tidy:
///
/// COM delivers calls into an STA as window messages. Without a thread running a message
/// loop, event subscriptions appear to succeed and then simply never fire — no error, no
/// exception, just silence. Pumping is the whole job of this class.
///
/// And it is deliberately not the WPF UI thread. Reading <c>MailItem.Body</c> on a cold
/// item can block noticeably, and that must never freeze the window. It also means
/// Outlook connectivity is independent of whether any window is open.
///
/// This is a raw <c>GetMessage</c> loop rather than WPF's <c>Dispatcher.Run()</c>, which
/// does the same thing underneath. Using it directly keeps this assembly free of WPF, so
/// the COM layer compiles anywhere the Windows targeting pack is available instead of
/// requiring a Windows host — which is what lets CI and a Linux dev box type-check it.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class OutlookStaThread : IAsyncDisposable
{
    private readonly Thread _thread;
    private readonly ConcurrentQueue<Action> _work = new();

    private readonly TaskCompletionSource _ready =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private uint _threadId;
    private volatile bool _disposed;

    /// <summary>Private message asking the pump to drain the work queue.</summary>
    private const uint WmRunWork = WmApp + 1;

    private const uint WmApp = 0x8000;
    private const uint WmQuit = 0x0012;
    private const uint PmNoRemove = 0x0000;

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
        _threadId = NativeMethods.GetCurrentThreadId();

        // A thread has no message queue until it first asks for one. Forcing creation
        // here means a PostThreadMessage that races startup cannot be silently dropped.
        NativeMethods.PeekMessage(out _, IntPtr.Zero, 0, 0, PmNoRemove);

        _ready.SetResult();

        // GetMessage returns 0 on WM_QUIT and -1 on error; both end the loop.
        while (NativeMethods.GetMessage(out var message, IntPtr.Zero, 0, 0) > 0)
        {
            if (message.Message == WmRunWork)
            {
                DrainWork();
                continue;
            }

            NativeMethods.TranslateMessage(ref message);
            NativeMethods.DispatchMessage(ref message);
        }

        // Anything queued between the quit request and the loop exiting still needs to
        // run, or its awaiting task never completes.
        DrainWork();
    }

    private void DrainWork()
    {
        while (_work.TryDequeue(out var action))
        {
            action();
        }
    }

    public Task ReadyAsync() => _ready.Task;

    public async Task<T> InvokeAsync<T>(Func<T> work)
    {
        await _ready.Task;

        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);

        Enqueue(() =>
        {
            try
            {
                completion.SetResult(work());
            }
            catch (Exception ex)
            {
                completion.SetException(ex);
            }
        });

        return await completion.Task;
    }

    public async Task InvokeAsync(Action work)
    {
        await _ready.Task;

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        Enqueue(() =>
        {
            try
            {
                work();
                completion.SetResult();
            }
            catch (Exception ex)
            {
                completion.SetException(ex);
            }
        });

        await completion.Task;
    }

    /// <summary>Fire-and-forget, for COM event handlers that must return immediately.</summary>
    public void Post(Action work)
    {
        if (!_ready.Task.IsCompleted || _disposed)
        {
            return;
        }

        Enqueue(work);
    }

    private void Enqueue(Action work)
    {
        _work.Enqueue(work);

        // Wake the pump. If this fails the thread is gone, and the queued item will be
        // picked up by the final drain (or never, if we are already shutting down).
        NativeMethods.PostThreadMessage(_threadId, WmRunWork, IntPtr.Zero, IntPtr.Zero);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (!_ready.Task.IsCompleted)
        {
            return;
        }

        NativeMethods.PostThreadMessage(_threadId, WmQuit, IntPtr.Zero, IntPtr.Zero);

        await Task.Run(() => _thread.Join(TimeSpan.FromSeconds(5)));
    }
}
