using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using OutlookScraper.App.Notifications;
using OutlookScraper.Core.Blacklist;
using OutlookScraper.Core.Models;
using OutlookScraper.Core.Ollama;
using OutlookScraper.Core.Scheduling;
using OutlookScraper.Core.Settings;
using OutlookScraper.Core.Storage;
using OutlookScraper.Outlook;

namespace OutlookScraper.App;

/// <summary>
/// Owns the running pipeline: mail arrivals in, suggestions out.
/// </summary>
/// <remarks>
/// The UI is a view onto this, not the other way round. Everything here keeps running
/// with no window open, which is the entire point of a tray application.
/// </remarks>
[SupportedOSPlatform("windows10.0.19041.0")]
public sealed class AppHost : IAsyncDisposable
{
    private readonly OutlookMailSource _mailSource;
    private readonly ClassificationQueue _queue;
    private readonly ClassificationWorker _worker;
    private readonly OllamaHealthMonitor _health;
    private readonly BlacklistService _blacklist;
    private readonly ProcessedMessageRepository _messages;
    private readonly SuggestionRepository _suggestions;
    private readonly StateRepository _state;
    private readonly ToastActivationRouter _router;
    private readonly SuggestionActions _actions;
    private readonly AppSettings _settings;
    private readonly ILogger<AppHost>? _logger;

    private readonly CancellationTokenSource _stopping = new();
    private Task? _workerLoop;
    private Task? _activationLoop;
    private Task? _sweepLoop;

    public AppHost(
        OutlookMailSource mailSource,
        ClassificationQueue queue,
        ClassificationWorker worker,
        OllamaHealthMonitor health,
        BlacklistService blacklist,
        ProcessedMessageRepository messages,
        SuggestionRepository suggestions,
        StateRepository state,
        ToastActivationRouter router,
        SuggestionActions actions,
        AppSettings settings,
        ILogger<AppHost>? logger = null)
    {
        _mailSource = mailSource;
        _queue = queue;
        _worker = worker;
        _health = health;
        _blacklist = blacklist;
        _messages = messages;
        _suggestions = suggestions;
        _state = state;
        _router = router;
        _actions = actions;
        _settings = settings;
        _logger = logger;

        _mailSource.EntryIdArrived += OnEntryIdArrived;
        _mailSource.StateChanged += (_, s) => MailStateChanged?.Invoke(this, s);
        _mailSource.Reconnected += async (_, _) => await CatchUpAsync();

        _health.HealthChanged += (_, h) => OllamaHealthChanged?.Invoke(this, h);
        _worker.ProgressChanged += async (_, _) => await RaisePendingCountAsync();
    }

    public event EventHandler<MailSourceState>? MailStateChanged;

    public event EventHandler<OllamaHealth>? OllamaHealthChanged;

    public event EventHandler<int>? PendingCountChanged;

    /// <summary>Raised when a toast asks for the review window.</summary>
    public event EventHandler<Guid>? ReviewRequested;

    public async Task StartAsync()
    {
        var ct = _stopping.Token;

        await _mailSource.StartAsync(ct);

        _health.Start();
        _workerLoop = Task.Run(() => _worker.RunAsync(ct), CancellationToken.None);
        _activationLoop = Task.Run(() => ConsumeActivationsAsync(ct), CancellationToken.None);
        _sweepLoop = Task.Run(() => SweepPeriodicallyAsync(ct), CancellationToken.None);

        // Any rule that predates the embedding model — or was made under a different
        // one — gets a comparable vector now.
        _ = Task.Run(() => _blacklist.BackfillEmbeddingsAsync(ct), CancellationToken.None);

        await RunRetentionAsync(ct);
        await RaisePendingCountAsync();
    }

    /// <summary>Enqueued as live so a new arrival always jumps ahead of a backfill.</summary>
    private void OnEntryIdArrived(object? sender, string entryId) =>
        _ = _queue.EnqueueLiveAsync(entryId, _stopping.Token).AsTask();

    /// <summary>
    /// The mandatory safety net. Outlook's ItemAdd event does not fire when more than
    /// sixteen items arrive at once, so without this a listserv burst is simply missed.
    /// </summary>
    private async Task SweepPeriodicallyAsync(CancellationToken ct)
    {
        await CatchUpAsync();

        using var timer = new PeriodicTimer(
            TimeSpan.FromMinutes(Math.Max(1, _settings.Mail.PollIntervalMinutes)));

        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                await CatchUpAsync();
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
    }

    /// <summary>
    /// Sweeps everything since the last confirmed watermark. Also runs after a
    /// reconnect, which is what makes "Outlook was closed overnight" a non-event.
    /// </summary>
    public async Task CatchUpAsync()
    {
        var ct = _stopping.Token;

        try
        {
            var since = await _state.GetTimestampAsync(StateRepository.LastSweepUtc, ct)
                        ?? DateTimeOffset.Now.AddDays(-Math.Max(1, _settings.Mail.BackfillDays));

            var isFirstRun = !await _state.GetFlagAsync(StateRepository.BackfillCompleted, ct);

            var messages = await _mailSource.SweepAsync(since, _settings.Mail.MaxSweepItems, ct);

            if (messages.Count == 0)
            {
                return;
            }

            _logger?.LogInformation("Sweep found {Count} message(s) since {Since}.", messages.Count, since);

            foreach (var email in messages)
            {
                // A first run is backfill; later sweeps are catching up on live mail
                // the events may have missed, so they keep live priority.
                if (isFirstRun)
                {
                    await _queue.EnqueueBackfillAsync(email.EntryId, ct);
                }
                else
                {
                    await _queue.EnqueueLiveAsync(email.EntryId, ct);
                }
            }

            // Deliberately NOT advanced here. The watermark only moves once every
            // message in the batch has reached a terminal state — advancing on enqueue
            // would permanently lose mail if Ollama went down mid-backfill.
            await AdvanceWatermarkIfDrainedAsync(messages, ct);

            if (isFirstRun)
            {
                await _state.SetFlagAsync(StateRepository.BackfillCompleted, true, ct);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Sweep failed.");
        }
    }

    private async Task AdvanceWatermarkIfDrainedAsync(
        IReadOnlyList<RawEmail> batch, CancellationToken ct)
    {
        foreach (var email in batch)
        {
            var record = await _messages.GetAsync(email.EntryId, ct);

            if (record is null || !record.IsTerminal)
            {
                return;
            }
        }

        var newest = batch.Max(m => m.ReceivedLocal);
        await _state.SetTimestampAsync(StateRepository.LastSweepUtc, newest, ct);
    }

    /// <summary>
    /// Applies toast button presses. Runs off the COM callback thread so the handler
    /// itself can return immediately.
    /// </summary>
    private async Task ConsumeActivationsAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var action in _router.Actions.ReadAllAsync(ct))
            {
                switch (action.Action)
                {
                    case ToastService.ActionAdd:
                        await _actions.AddToCalendarAsync(action.SuggestionId, ct);
                        break;

                    case ToastService.ActionBlacklist:
                        await _actions.BlacklistAsync(action.SuggestionId, ct);
                        break;

                    default:
                        ReviewRequested?.Invoke(this, action.SuggestionId);
                        break;
                }

                await RaisePendingCountAsync();
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
    }

    /// <summary>Re-queues everything that failed, for the tray menu command.</summary>
    public async Task RetryFailedAsync()
    {
        var ct = _stopping.Token;
        var failed = await _messages.GetFailedEntryIdsAsync(maxAttempts: 5, ct);

        foreach (var entryId in failed)
        {
            await _messages.ResetToQueuedAsync(entryId, ct);
            await _queue.EnqueueLiveAsync(entryId, ct);
        }

        _logger?.LogInformation("Re-queued {Count} failed message(s).", failed.Count);
    }

    private async Task RunRetentionAsync(CancellationToken ct)
    {
        try
        {
            var cutoff = DateTimeOffset.UtcNow.AddDays(-_settings.General.RetentionDays);
            var rawCutoff = DateTimeOffset.UtcNow.AddDays(-_settings.General.RawJsonRetentionDays);

            var removed = await _messages.PruneAsync(cutoff, rawCutoff, ct);

            if (removed > 0)
            {
                _logger?.LogInformation("Retention removed {Count} old message(s).", removed);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Retention pass failed.");
        }
    }

    private async Task RaisePendingCountAsync()
    {
        try
        {
            var count = await _suggestions.CountByStateAsync(
                SuggestionState.Pending, _stopping.Token);

            PendingCountChanged?.Invoke(this, count);
        }
        catch (Exception)
        {
            // Counting for a tray tooltip must never take the app down.
        }
    }

    public int QueueDepth => _queue.PendingTotal;

    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync();

        foreach (var loop in new[] { _workerLoop, _activationLoop, _sweepLoop })
        {
            if (loop is null)
            {
                continue;
            }

            try
            {
                await loop.WaitAsync(TimeSpan.FromSeconds(3));
            }
            catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
            {
                // Shutting down regardless.
            }
        }

        _health.Dispose();
        await _mailSource.DisposeAsync();
        _stopping.Dispose();
    }
}
