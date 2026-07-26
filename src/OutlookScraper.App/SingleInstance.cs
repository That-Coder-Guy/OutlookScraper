using System.IO;
using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Text;

namespace OutlookScraper.App;

/// <summary>
/// Ensures one running copy, and forwards a second launch's arguments to it.
/// </summary>
/// <remarks>
/// Worth being precise about what this does and does not cover. It handles the user
/// double-clicking the exe again while the tray app is running.
///
/// It deliberately does <b>not</b> handle toast activation of an already-running app:
/// the notification compat layer registers a COM server owned by the running process,
/// so Windows activates it in-process and the activation event fires directly in the
/// live instance without ever touching this pipe.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class SingleInstance : IDisposable
{
    private const string MutexName = @"Local\OutlookScraper.SingleInstance";
    private const string PipeName = "OutlookScraper.Ipc";

    private readonly Mutex _mutex;
    private CancellationTokenSource? _listening;

    private SingleInstance(Mutex mutex, bool isPrimary)
    {
        _mutex = mutex;
        IsPrimary = isPrimary;
    }

    /// <summary>False when another copy already owns the mutex.</summary>
    public bool IsPrimary { get; }

    public static SingleInstance Acquire()
    {
        var mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
        return new SingleInstance(mutex, createdNew);
    }

    /// <summary>Raised when another launch forwards its command line.</summary>
    public event Action<string[]>? SecondInstanceLaunched;

    public void StartListening()
    {
        if (!IsPrimary)
        {
            return;
        }

        _listening = new CancellationTokenSource();
        _ = Task.Run(() => ListenAsync(_listening.Token));
    }

    private async Task ListenAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(
                    PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

                await server.WaitForConnectionAsync(ct);

                using var reader = new StreamReader(server, Encoding.UTF8);
                var payload = await reader.ReadToEndAsync(ct);

                var args = payload.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                SecondInstanceLaunched?.Invoke(args);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (IOException)
            {
                // Client vanished mid-handshake; just wait for the next one.
            }
        }
    }

    /// <summary>Hands arguments to the running instance. Best-effort by design.</summary>
    public static void ForwardToPrimary(string[] args)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(2000);

            using var writer = new StreamWriter(client, Encoding.UTF8);
            writer.Write(string.Join('\n', args));
        }
        catch (Exception ex) when (ex is TimeoutException or IOException)
        {
            // The primary is busy or shutting down. Nothing useful to do — this process
            // is exiting regardless.
        }
    }

    public void Dispose()
    {
        _listening?.Cancel();
        _listening?.Dispose();

        if (IsPrimary)
        {
            try
            {
                _mutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
                // Not owned; already released.
            }
        }

        _mutex.Dispose();
    }
}
