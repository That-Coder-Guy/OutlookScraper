using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using OutlookScraper.Core.Abstractions;
using OutlookScraper.Core.Models;
using OutlookScraper.Core.Settings;

namespace OutlookScraper.Outlook;

/// <summary>
/// <see cref="IMailSource"/> over classic Outlook, with a supervisor that keeps the
/// connection alive across Outlook being closed and reopened.
/// </summary>
/// <remarks>
/// Mail is delivered by three redundant paths, and all of them are expected to overlap:
///
/// <list type="bullet">
/// <item><c>Items.ItemAdd</c> — lowest latency, and the only one that fires for mail a
/// rule moved into a subfolder.</item>
/// <item><c>Application.NewMailEx</c> — cheap redundancy for the default inbox.</item>
/// <item>A periodic <c>Restrict</c> sweep — <b>mandatory</b>, not a nicety.
/// <c>ItemAdd</c> is documented not to fire when more than sixteen items arrive at
/// once, which is exactly what a listserv digest burst looks like.</item>
/// </list>
///
/// Triple delivery is harmless because the pipeline claims each EntryID exactly once
/// before doing any work.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class OutlookMailSource : IMailSource
{
    private readonly OutlookStaThread _sta;
    private readonly OutlookConnection _connection;
    private readonly MailSettings _settings;
    private readonly ILogger<OutlookMailSource>? _logger;
    private readonly CancellationTokenSource _stopping = new();

    private Task? _supervisor;
    private MailSourceState _state = MailSourceState.Disconnected;

    /// <summary>HRESULTs that mean "Outlook is gone", as opposed to a transient error.</summary>
    private static readonly int[] DisconnectHResults =
    [
        unchecked((int)0x800706BA), // RPC_S_SERVER_UNAVAILABLE
        unchecked((int)0x80010108), // RPC_E_DISCONNECTED
        unchecked((int)0x800401E3), // MK_E_UNAVAILABLE
        unchecked((int)0x80080005), // CO_E_SERVER_EXEC_FAILURE
    ];

    private static readonly TimeSpan ProbeInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan WaitForOutlookInterval = TimeSpan.FromSeconds(15);

    /// <summary>Reconnect backoff, capped rather than unbounded.</summary>
    private static readonly TimeSpan[] ReconnectBackoff =
    [
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(10),
        TimeSpan.FromSeconds(30),
        TimeSpan.FromSeconds(60),
    ];

    public OutlookMailSource(MailSettings settings, ILogger<OutlookMailSource>? logger = null)
    {
        _settings = settings;
        _logger = logger;
        _sta = new OutlookStaThread();
        _connection = new OutlookConnection(logger);

        _connection.EntryIdArrived += OnEntryIdArrived;
        _connection.HostQuitting += OnHostQuitting;
    }

    public event EventHandler<MailSourceState>? StateChanged;

    /// <inheritdoc />
    public event EventHandler<string>? EntryIdArrived;

    /// <inheritdoc />
    public event EventHandler? Reconnected;

    public MailSourceState State
    {
        get => _state;
        private set
        {
            if (_state == value)
            {
                return;
            }

            _state = value;
            StateChanged?.Invoke(this, value);
        }
    }

    public async Task StartAsync(CancellationToken ct)
    {
        await _sta.ReadyAsync();
        _supervisor ??= Task.Run(() => SuperviseAsync(_stopping.Token), CancellationToken.None);
    }

    /// <summary>
    /// The state machine: Disconnected → WaitingForHost → Connecting → Connected, with
    /// Faulted on the way back round.
    /// </summary>
    private async Task SuperviseAsync(CancellationToken ct)
    {
        var attempt = 0;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (State == MailSourceState.Connected)
                {
                    await Task.Delay(ProbeInterval, ct);

                    if (!await ProbeAsync())
                    {
                        await FaultAsync();
                        attempt = 0;
                    }

                    continue;
                }

                State = MailSourceState.Connecting;

                if (await TryConnectAsync())
                {
                    State = MailSourceState.Connected;
                    attempt = 0;
                    Reconnected?.Invoke(this, EventArgs.Empty);
                    continue;
                }

                // Outlook simply is not running. That is an ordinary state — wait for
                // the user to open it rather than launching it ourselves.
                State = MailSourceState.WaitingForHost;
                await Task.Delay(
                    attempt == 0 ? WaitForOutlookInterval : BackoffFor(attempt), ct);

                attempt++;
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Outlook supervisor error.");
                await FaultAsync();
                await Task.Delay(BackoffFor(++attempt), ct);
            }
        }
    }

    private static TimeSpan BackoffFor(int attempt) =>
        ReconnectBackoff[Math.Min(attempt, ReconnectBackoff.Length - 1)];

    private Task<bool> TryConnectAsync() =>
        _sta.InvokeAsync(() =>
        {
            try
            {
                return _connection.TryConnect(_settings.WatchedFolders);
            }
            catch (COMException ex)
            {
                _logger?.LogWarning("Connect failed: {Message}", ex.Message);
                return false;
            }
        });

    private Task<bool> ProbeAsync() =>
        _sta.InvokeAsync(() =>
        {
            try
            {
                _connection.Probe();
                return true;
            }
            catch (COMException ex) when (IsDisconnect(ex))
            {
                _logger?.LogInformation("Outlook connection lost ({HResult:X}).", ex.HResult);
                return false;
            }
            catch (COMException ex)
            {
                // Something else went wrong; keep the connection rather than
                // tearing down on a transient hiccup.
                _logger?.LogDebug("Probe hiccup: {Message}", ex.Message);
                return true;
            }
        });

    private static bool IsDisconnect(COMException ex) => DisconnectHResults.Contains(ex.HResult);

    /// <summary>
    /// Tears the whole graph down. Wrappers are never reused across a reconnect — the
    /// entire object graph is rebuilt from a fresh attach.
    /// </summary>
    private async Task FaultAsync()
    {
        State = MailSourceState.Faulted;

        await _sta.InvokeAsync(() =>
        {
            try
            {
                _connection.Teardown();
            }
            catch (COMException)
            {
                // Expected when Outlook has already exited.
            }
        });

        State = MailSourceState.Disconnected;
    }

    private void OnEntryIdArrived(string entryId) =>
        EntryIdArrived?.Invoke(this, entryId);

    private void OnHostQuitting()
    {
        _logger?.LogInformation("Outlook is shutting down; releasing the connection.");
        _ = FaultAsync();
    }

    public async Task<IReadOnlyList<RawEmail>> SweepAsync(
        DateTimeOffset sinceLocal, int max, CancellationToken ct)
    {
        if (State != MailSourceState.Connected)
        {
            return [];
        }

        return await _sta.InvokeAsync<IReadOnlyList<RawEmail>>(() =>
        {
            try
            {
                return _connection.Sweep(sinceLocal, max);
            }
            catch (COMException ex)
            {
                _logger?.LogWarning("Sweep failed: {Message}", ex.Message);
                return [];
            }
        });
    }

    public async Task<RawEmail?> GetByEntryIdAsync(string entryId, CancellationToken ct)
    {
        if (State != MailSourceState.Connected)
        {
            return null;
        }

        return await _sta.InvokeAsync(() =>
        {
            try
            {
                return _connection.GetByEntryId(entryId);
            }
            catch (COMException)
            {
                return null;
            }
        });
    }

    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync();

        if (_supervisor is not null)
        {
            try
            {
                await _supervisor.WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
            {
                // Shutting down anyway.
            }
        }

        await _sta.InvokeAsync(() =>
        {
            try
            {
                _connection.Teardown();
            }
            catch (COMException)
            {
                // Outlook already gone.
            }
        });

        await _sta.DisposeAsync();
        _stopping.Dispose();
    }
}
