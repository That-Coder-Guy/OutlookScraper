using System.Threading.Channels;
using OutlookScraper.Core.Models;

namespace OutlookScraper.Core.Scheduling;

/// <summary>
/// Two bounded queues with a strict priority between them.
/// </summary>
/// <remarks>
/// Live mail is always drained before backfill, so kicking off a 500-message first-run
/// sweep never delays classification of an email that just arrived.
///
/// Both are bounded and both block the producer when full, which is deliberate: the
/// producer is the Outlook sweep, and making it wait is exactly right. Dropping items
/// would mean silently losing mail.
/// </remarks>
public sealed class ClassificationQueue(int liveCapacity = 200, int backfillCapacity = 2000)
{
    private readonly Channel<string> _live = Channel.CreateBounded<string>(
        new BoundedChannelOptions(liveCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
        });

    private readonly Channel<string> _backfill = Channel.CreateBounded<string>(
        new BoundedChannelOptions(backfillCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
        });

    public int PendingLive => _live.Reader.Count;

    public int PendingBackfill => _backfill.Reader.Count;

    public int PendingTotal => PendingLive + PendingBackfill;

    public ValueTask EnqueueLiveAsync(string entryId, CancellationToken ct = default) =>
        _live.Writer.WriteAsync(entryId, ct);

    public ValueTask EnqueueBackfillAsync(string entryId, CancellationToken ct = default) =>
        _backfill.Writer.WriteAsync(entryId, ct);

    /// <summary>
    /// Takes the next item, preferring live over backfill. Waits for either channel
    /// when both are empty.
    /// </summary>
    public async Task<QueuedItem?> DequeueAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            if (_live.Reader.TryRead(out var live))
            {
                return new QueuedItem(live, ProcessingPriority.Live);
            }

            if (_backfill.Reader.TryRead(out var backfill))
            {
                return new QueuedItem(backfill, ProcessingPriority.Backfill);
            }

            // Nothing ready. Wake on whichever channel produces first, then re-check
            // in priority order rather than consuming from the one that woke us.
            var liveWait = _live.Reader.WaitToReadAsync(ct).AsTask();
            var backfillWait = _backfill.Reader.WaitToReadAsync(ct).AsTask();

            try
            {
                await Task.WhenAny(liveWait, backfillWait);
            }
            catch (OperationCanceledException)
            {
                return null;
            }
        }

        return null;
    }

    public void CompleteBackfill() => _backfill.Writer.TryComplete();
}

public sealed record QueuedItem(string EntryId, ProcessingPriority Priority);
