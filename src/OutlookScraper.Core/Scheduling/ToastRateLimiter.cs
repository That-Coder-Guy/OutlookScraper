using OutlookScraper.Core.Abstractions;
using OutlookScraper.Core.Settings;

namespace OutlookScraper.Core.Scheduling;

/// <summary>
/// Keeps a busy morning from turning into a wall of notifications.
/// </summary>
/// <remarks>
/// Campus mail arrives in bursts — a dozen announcements can land within a minute of
/// each other. Past the configured budget, further detections are counted rather than
/// toasted, and the caller flushes them as a single "N more free-food events found"
/// summary.
/// </remarks>
public sealed class ToastRateLimiter(GeneralSettings settings, IClock clock)
{
    private readonly GeneralSettings _settings = settings;
    private readonly IClock _clock = clock;
    private readonly Queue<DateTimeOffset> _recent = new();
    private readonly object _gate = new();

    private int _suppressed;

    /// <summary>Number of detections withheld since the last flush.</summary>
    public int PendingSummaryCount
    {
        get
        {
            lock (_gate)
            {
                return _suppressed;
            }
        }
    }

    /// <summary>
    /// Whether this detection should get its own toast. A false result means the caller
    /// should stay silent now and rely on the summary later.
    /// </summary>
    public bool TryClaim()
    {
        lock (_gate)
        {
            var now = _clock.UtcNow;
            var cutoff = now - _settings.ToastWindow;

            while (_recent.Count > 0 && _recent.Peek() < cutoff)
            {
                _recent.Dequeue();
            }

            if (_recent.Count >= _settings.MaxToastsPerWindow)
            {
                _suppressed++;
                return false;
            }

            _recent.Enqueue(now);
            return true;
        }
    }

    /// <summary>Returns the withheld count and resets it, for emitting one summary toast.</summary>
    public int FlushSummaryCount()
    {
        lock (_gate)
        {
            var count = _suppressed;
            _suppressed = 0;
            return count;
        }
    }
}
