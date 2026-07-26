using OutlookScraper.Core.Abstractions;

namespace OutlookScraper.Core.Scheduling;

public enum CircuitState
{
    Closed = 0,
    Open,
    HalfOpen,
}

/// <summary>
/// Stops the worker hammering a local Ollama server that is down.
/// </summary>
/// <remarks>
/// The important property is that opening the circuit does not *lose* anything. Jobs
/// stay queued and the sweep watermark is not advanced, so when Ollama comes back the
/// backlog is simply worked through. That is the whole reason this exists rather than
/// letting each request fail on its own.
/// </remarks>
public sealed class CircuitBreaker(IClock clock, int failureThreshold = 3, TimeSpan? resetAfter = null)
{
    private readonly IClock _clock = clock;
    private readonly int _failureThreshold = failureThreshold;
    private readonly TimeSpan _resetAfter = resetAfter ?? TimeSpan.FromSeconds(60);
    private readonly object _gate = new();

    private int _consecutiveFailures;
    private DateTimeOffset _openedAt;
    private CircuitState _state = CircuitState.Closed;

    public CircuitState State
    {
        get
        {
            lock (_gate)
            {
                // Opening is time-based, so the transition to half-open has to be
                // evaluated on read rather than by a timer.
                if (_state == CircuitState.Open && _clock.UtcNow - _openedAt >= _resetAfter)
                {
                    _state = CircuitState.HalfOpen;
                }

                return _state;
            }
        }
    }

    /// <summary>False while the circuit is open; a single probe is allowed when half-open.</summary>
    public bool CanExecute => State != CircuitState.Open;

    public void RecordSuccess()
    {
        lock (_gate)
        {
            _consecutiveFailures = 0;
            _state = CircuitState.Closed;
        }
    }

    public void RecordFailure()
    {
        lock (_gate)
        {
            _consecutiveFailures++;

            // A failed probe while half-open re-opens immediately rather than needing
            // to accumulate the full threshold again.
            if (_state == CircuitState.HalfOpen || _consecutiveFailures >= _failureThreshold)
            {
                _state = CircuitState.Open;
                _openedAt = _clock.UtcNow;
            }
        }
    }

    public void Reset()
    {
        lock (_gate)
        {
            _consecutiveFailures = 0;
            _state = CircuitState.Closed;
        }
    }
}
