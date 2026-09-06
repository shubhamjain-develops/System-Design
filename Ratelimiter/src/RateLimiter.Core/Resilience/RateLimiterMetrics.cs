namespace RateLimiter.Core.Resilience;

/// <summary>
/// Counters describing what the limiter has actually been doing.
/// </summary>
/// <remarks>
/// <para>
/// These exist because of the one failure mode that is otherwise invisible. A limiter that has
/// stopped limiting — because the store is unreachable and the policy is to fail open — serves
/// every request successfully and reports nothing wrong. No error rate moves. No latency moves.
/// The first symptom is a bill, or an outage somewhere downstream.
/// </para>
/// <para>
/// <see cref="StoreFailures"/> is the number worth alerting on: it should be zero, and any
/// sustained non-zero value means limits are not being enforced no matter how healthy everything
/// else looks.
/// </para>
/// <para>
/// Counters are monotonic and read with <see cref="Interlocked"/>, so they are safe to sample
/// from a metrics thread while requests are in flight. A snapshot may be very slightly
/// inconsistent between fields, which is the right trade for never contending with the request
/// path.
/// </para>
/// </remarks>
public sealed class RateLimiterMetrics
{
    private long _allowed;
    private long _rejected;
    private long _storeFailures;
    private long _shortCircuited;
    private long _writeContentionExhausted;

    /// <summary>Requests admitted, including those admitted by a fail-open.</summary>
    public long Allowed => Interlocked.Read(ref _allowed);

    /// <summary>Requests refused, including those refused by a fail-closed.</summary>
    public long Rejected => Interlocked.Read(ref _rejected);

    /// <summary>
    /// Requests decided by the failure policy rather than by an algorithm.
    /// </summary>
    /// <remarks>
    /// The alerting signal. Any sustained non-zero value means the configured limits are not
    /// being enforced, while everything else about the service looks healthy.
    /// </remarks>
    public long StoreFailures => Interlocked.Read(ref _storeFailures);

    /// <summary>
    /// Requests that skipped the store entirely because the circuit breaker was open.
    /// </summary>
    /// <remarks>
    /// A subset of <see cref="StoreFailures"/>. When this is close to the total, the breaker is
    /// doing its job: the store is not being called at all, so it is not being hammered while it
    /// tries to recover.
    /// </remarks>
    public long ShortCircuited => Interlocked.Read(ref _shortCircuited);

    /// <summary>
    /// Requests that lost every compare-and-swap attempt.
    /// </summary>
    /// <remarks>
    /// The signal that says whether the CAS design is holding. A rising count means contention
    /// on individual keys is high enough that server-side evaluation — the Lua path documented
    /// in the Redis adapter — has become the better choice. This is the number to watch when
    /// deciding to switch, rather than requests per second.
    /// </remarks>
    public long WriteContentionExhausted => Interlocked.Read(ref _writeContentionExhausted);

    internal void RecordAllowed() => Interlocked.Increment(ref _allowed);

    internal void RecordRejected() => Interlocked.Increment(ref _rejected);

    internal void RecordStoreFailure() => Interlocked.Increment(ref _storeFailures);

    internal void RecordShortCircuit() => Interlocked.Increment(ref _shortCircuited);

    internal void RecordWriteContentionExhausted() => Interlocked.Increment(ref _writeContentionExhausted);

    /// <summary>
    /// Renders the counters for demo output and logs.
    /// </summary>
    /// <returns>A one-line summary.</returns>
    public override string ToString() =>
        $"allowed={Allowed} rejected={Rejected} storeFailures={StoreFailures} "
        + $"shortCircuited={ShortCircuited} contentionExhausted={WriteContentionExhausted}";
}
