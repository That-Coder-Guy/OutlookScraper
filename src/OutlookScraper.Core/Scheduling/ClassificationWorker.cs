using Microsoft.Extensions.Logging;
using OutlookScraper.Core.Abstractions;
using OutlookScraper.Core.Models;
using OutlookScraper.Core.Pipeline;

namespace OutlookScraper.Core.Scheduling;

/// <summary>
/// The single consumer that drains the classification queue.
/// </summary>
/// <remarks>
/// Deliberately one consumer, not a pool. Ollama serializes requests against a single
/// loaded model anyway, so concurrency buys no throughput and costs VRAM. The real
/// throughput lever is <c>keep_alive</c> on the request, which stops the model being
/// unloaded and reloaded between emails.
/// </remarks>
public sealed class ClassificationWorker(
    ClassificationQueue queue,
    MailPipeline pipeline,
    IMailSource mailSource,
    INotifier notifier,
    ToastRateLimiter rateLimiter,
    CircuitBreaker breaker,
    ILogger<ClassificationWorker>? logger = null)
{
    private readonly ClassificationQueue _queue = queue;
    private readonly MailPipeline _pipeline = pipeline;
    private readonly IMailSource _mailSource = mailSource;
    private readonly INotifier _notifier = notifier;
    private readonly ToastRateLimiter _rateLimiter = rateLimiter;
    private readonly CircuitBreaker _breaker = breaker;
    private readonly ILogger<ClassificationWorker>? _logger = logger;

    /// <summary>Yield period during backfill, so a big sweep leaves the GPU usable.</summary>
    private const int BackfillYieldEvery = 25;

    private static readonly TimeSpan BackfillYieldDelay = TimeSpan.FromMilliseconds(250);

    /// <summary>How long to wait before re-checking a queue held up by an open circuit.</summary>
    private static readonly TimeSpan CircuitRetryDelay = TimeSpan.FromSeconds(10);

    private int _backfillProcessed;
    private bool _circuitReported;

    /// <summary>Raised whenever counts change, so the tray badge can follow along.</summary>
    public event EventHandler? ProgressChanged;

    public async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var item = await _queue.DequeueAsync(ct);

            if (item is null)
            {
                break;
            }

            // While the circuit is open the item is put back and nothing is lost. The
            // sweep watermark is not advanced either, so an Ollama outage delays work
            // rather than dropping mail.
            if (!_breaker.CanExecute)
            {
                // Logged once per open period, not once per retry: this loop spins every
                // ten seconds, and an unlogged stall is indistinguishable from an empty
                // queue — which is precisely how "nothing is showing up" gets misdiagnosed.
                if (!_circuitReported)
                {
                    _circuitReported = true;

                    _logger?.LogWarning(
                        "Classification paused: too many consecutive failures, so the circuit "
                        + "breaker is open. {Count} message(s) are waiting and will be retried "
                        + "every {Seconds:n0}s. Nothing is lost.",
                        _queue.PendingTotal, CircuitRetryDelay.TotalSeconds);
                }

                await RequeueAsync(item, ct);
                await Task.Delay(CircuitRetryDelay, ct);
                continue;
            }

            if (_circuitReported)
            {
                _circuitReported = false;
                _logger?.LogInformation("Classification resumed; working through the queue again.");
            }

            await ProcessOneAsync(item, ct);
        }
    }

    private async Task ProcessOneAsync(QueuedItem item, CancellationToken ct)
    {
        var id = ShortId(item.EntryId);

        try
        {
            _logger?.LogDebug(
                "[{Id}] dequeued as {Priority}; {Remaining} left in the queue.",
                id, item.Priority, _queue.PendingTotal);

            var email = await _mailSource.GetByEntryIdAsync(item.EntryId, ct);

            if (email is null)
            {
                // Moved or deleted between enqueue and processing. Nothing to do.
                _logger?.LogInformation(
                    "[{Id}] no longer in Outlook (moved or deleted since it was queued); skipping.",
                    id);

                return;
            }

            var outcome = await _pipeline.ProcessAsync(email, ct);

            if (outcome.Kind == PipelineOutcomeKind.Failed)
            {
                _breaker.RecordFailure();
            }
            else
            {
                _breaker.RecordSuccess();
            }

            if (outcome is { Kind: PipelineOutcomeKind.Suggested, Suggestion: not null })
            {
                await NotifyAsync(id, outcome.Suggestion, item.Priority);
            }

            ProgressChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _breaker.RecordFailure();
            _logger?.LogError(ex, "[{Id}] unhandled error while processing.", id);
        }
        finally
        {
            if (item.Priority == ProcessingPriority.Backfill)
            {
                await YieldDuringBackfillAsync(ct);
            }
        }
    }

    /// <summary>
    /// Backfill never toasts. Working through a week of old mail one notification at a
    /// time would be intolerable; the caller emits a single summary when the sweep ends.
    /// </summary>
    private async Task NotifyAsync(string id, EventSuggestion suggestion, ProcessingPriority priority)
    {
        if (priority == ProcessingPriority.Backfill)
        {
            _logger?.LogInformation(
                "[{Id}] no toast (backfill); it is waiting in the Pending tab.", id);

            return;
        }

        if (_rateLimiter.TryClaim())
        {
            await _notifier.ShowSuggestionAsync(suggestion);
            _logger?.LogInformation("[{Id}] toast shown.", id);
            return;
        }

        // Over budget: fold this one into the running summary and emit that instead.
        var withheld = _rateLimiter.FlushSummaryCount();

        _logger?.LogInformation(
            "[{Id}] toast rate limit reached; folded into a summary of {Count}.", id, withheld);

        if (withheld > 0)
        {
            await _notifier.ShowSummaryAsync(withheld);
        }
    }

    private async Task YieldDuringBackfillAsync(CancellationToken ct)
    {
        if (++_backfillProcessed % BackfillYieldEvery != 0)
        {
            return;
        }

        try
        {
            await Task.Delay(BackfillYieldDelay, ct);
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
    }

    /// <summary>Tail of an EntryID; matches the id the pipeline logs, so lines correlate.</summary>
    private static string ShortId(string entryId) =>
        string.IsNullOrEmpty(entryId) ? "?" :
        entryId.Length <= 10 ? entryId : entryId[^10..];

    private ValueTask RequeueAsync(QueuedItem item, CancellationToken ct) =>
        item.Priority == ProcessingPriority.Live
            ? _queue.EnqueueLiveAsync(item.EntryId, ct)
            : _queue.EnqueueBackfillAsync(item.EntryId, ct);
}
