namespace RateLimiter.Core.Resilience;

/// <summary>
/// What to do when limiter state cannot be reached.
/// </summary>
/// <remarks>
/// <para>
/// There is no correct universal answer, which is why this is configuration and not a default
/// buried in the engine. The question it asks is: when the limiter cannot tell whether a caller
/// is over their limit, is it worse to serve a request that should have been refused, or to
/// refuse one that should have been served?
/// </para>
/// <para>
/// For a public API gateway the answer is usually the second, and so
/// <see cref="FailOpen"/> is the default: a limiter that takes the whole API down when its cache
/// blinks has caused a larger outage than the abuse it was protecting against. For a
/// paid-per-call resource, a login endpoint, or anything where exceeding the limit costs money
/// or admits an attack, the answer inverts and <see cref="FailClosed"/> is correct.
/// </para>
/// <para>
/// Either way the choice must be explicit and its effects visible. A limiter that has silently
/// stopped limiting looks exactly like one that is working — every response still says 200 —
/// which is why every decision made under a failure carries
/// <see cref="RateLimitDecision.StoreFailureOccurred"/>.
/// </para>
/// </remarks>
public enum StoreFailurePolicy
{
    /// <summary>
    /// Admit the request. Favours availability of the protected service over enforcement of the
    /// limit.
    /// </summary>
    FailOpen = 0,

    /// <summary>
    /// Refuse the request. Favours enforcement over availability, at the cost of turning a
    /// store outage into a service outage.
    /// </summary>
    FailClosed = 1,
}
