using RateLimiter.Core.Time;

namespace RateLimiter.Core.Resilience;

/// <summary>
/// Which way the breaker is currently set.
/// </summary>
public enum CircuitState
{
    /// <summary>Calls pass through normally.</summary>
    Closed = 0,

    /// <summary>Calls are short-circuited without being attempted.</summary>
    Open = 1,

    /// <summary>One trial call is permitted to test whether the dependency has recovered.</summary>
    HalfOpen = 2,
}

/// <summary>
/// Short-circuits calls to a dependency that has started failing.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why this exists rather than a per-request timeout.</strong> A timeout still pays the
/// timeout. If the store is unreachable and each request waits 50ms to discover that, then at a
/// thousand requests per second the limiter is holding fifty seconds of latency per second —
/// exhausting connections and threads in the service it was meant to protect. The failure mode
/// becomes the limiter, not the store.
/// </para>
/// <para>
/// A breaker converts that into a one-off cost. After a handful of failures it stops calling at
/// all, and every subsequent request resolves instantly through the failure policy. The store
/// gets a chance to recover instead of being hammered by retries, which is often what keeps an
/// overloaded cache from staying overloaded.
/// </para>
/// <para>
/// The half-open state is what makes recovery automatic without stampeding: exactly one request
/// is allowed through to test the water, and only its result decides whether the breaker closes.
/// Letting every request through the moment the timer expires would slam a recovering store with
/// full production load, which is a common way to knock it straight back over.
/// </para>
/// </remarks>
public sealed class CircuitBreaker
{
    private readonly int _failureThreshold;
    private readonly TimeSpan _openDuration;
    private readonly IClock _clock;
    private readonly Lock _gate = new();

    private CircuitState _state = CircuitState.Closed;
    private int _consecutiveFailures;
    private long _openedAtTicks;
    private bool _trialInFlight;

    /// <summary>
    /// Creates a breaker.
    /// </summary>
    /// <param name="failureThreshold">
    /// Consecutive failures before the breaker opens. Must be at least one.
    /// </param>
    /// <param name="openDuration">How long to stay open before permitting a trial call.</param>
    /// <param name="clock">The clock. Defaults to the system clock.</param>
    /// <remarks>
    /// The threshold counts <em>consecutive</em> failures deliberately. A store that fails one
    /// call in a thousand is not broken, and a breaker that opened on cumulative failures would
    /// eventually open on every long-running process regardless of health.
    /// </remarks>
    public CircuitBreaker(int failureThreshold = 5, TimeSpan? openDuration = null, IClock? clock = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(failureThreshold, 1);

        _failureThreshold = failureThreshold;
        _openDuration = openDuration ?? TimeSpan.FromSeconds(5);
        _clock = clock ?? SystemClock.Instance;

        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(_openDuration, TimeSpan.Zero);
    }

    /// <summary>
    /// The current state.
    /// </summary>
    public CircuitState State
    {
        get
        {
            lock (_gate)
            {
                return _state;
            }
        }
    }

    /// <summary>
    /// Asks whether a call may be attempted.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> when the caller should attempt the operation and then report the
    /// outcome; <see langword="false"/> when it should be short-circuited.
    /// </returns>
    public bool TryEnter()
    {
        lock (_gate)
        {
            switch (_state)
            {
                case CircuitState.Closed:
                    return true;

                case CircuitState.Open:
                    if (_clock.UtcNow.UtcTicks - _openedAtTicks < _openDuration.Ticks)
                    {
                        return false;
                    }

                    // Time to test the water, with exactly one request.
                    _state = CircuitState.HalfOpen;
                    _trialInFlight = true;
                    return true;

                case CircuitState.HalfOpen:
                    if (_trialInFlight)
                    {
                        return false;
                    }

                    _trialInFlight = true;
                    return true;

                default:
                    return true;
            }
        }
    }

    /// <summary>
    /// Reports that an attempted call succeeded.
    /// </summary>
    public void RecordSuccess()
    {
        lock (_gate)
        {
            _consecutiveFailures = 0;
            _trialInFlight = false;
            _state = CircuitState.Closed;
        }
    }

    /// <summary>
    /// Reports that an attempted call failed.
    /// </summary>
    public void RecordFailure()
    {
        lock (_gate)
        {
            _trialInFlight = false;

            if (_state == CircuitState.HalfOpen)
            {
                // The trial failed, so the dependency has not recovered. Restart the clock
                // rather than counting toward the threshold again.
                _state = CircuitState.Open;
                _openedAtTicks = _clock.UtcNow.UtcTicks;
                return;
            }

            _consecutiveFailures++;

            if (_consecutiveFailures >= _failureThreshold)
            {
                _state = CircuitState.Open;
                _openedAtTicks = _clock.UtcNow.UtcTicks;
            }
        }
    }
}
