namespace RateLimiter.Core;

/// <summary>
/// How much traffic is allowed, over what period, using which algorithm.
/// </summary>
/// <remarks>
/// <para>
/// One policy shape serves all five algorithms, and that uniformity is the point rather than
/// a convenience. <see cref="Limit"/> permits per <see cref="Window"/> means the same thing
/// everywhere: it is the sustained rate the caller is entitled to. What differs between
/// algorithms is not the entitlement but the <em>shape</em> of traffic each will tolerate on
/// the way to it — how large a burst passes, and how abruptly the allowance returns.
/// </para>
/// <para>
/// That is what makes switching algorithms a configuration change rather than a
/// re-negotiation with the caller. Moving an endpoint from a fixed window to a token bucket
/// does not alter what was promised; it alters only whether a client that saves up its
/// allowance can spend it at once.
/// </para>
/// <para>
/// The alternative — a separate policy type per algorithm, with token bucket taking a
/// capacity and a refill rate while fixed window takes a count and a window — is more
/// faithful to each algorithm's own literature. It was rejected because it makes the rules
/// file algorithm-shaped: every rule would need to know which fields its algorithm wanted,
/// and changing the algorithm would mean rewriting the rule. The cost of the uniform shape is
/// <see cref="BurstCapacity"/>, below, which exists solely because token and leaky buckets
/// have one genuinely extra degree of freedom.
/// </para>
/// </remarks>
public sealed record RateLimitPolicy
{
    private readonly int _limit;
    private readonly TimeSpan _window;
    private readonly int? _burstCapacity;

    /// <summary>
    /// The algorithm that enforces this policy.
    /// </summary>
    public required RateLimitAlgorithm Algorithm { get; init; }

    /// <summary>
    /// The number of permits granted per <see cref="Window"/>.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not positive.</exception>
    /// <remarks>
    /// A limit of zero would mean "reject everything", which is a block, not a rate limit, and
    /// is better expressed by a component whose name says so. Rejecting it here means no
    /// algorithm has to carry a special case for it.
    /// </remarks>
    public required int Limit
    {
        get => _limit;
        init
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, 1);
            _limit = value;
        }
    }

    /// <summary>
    /// The period over which <see cref="Limit"/> permits are granted.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not positive.</exception>
    public required TimeSpan Window
    {
        get => _window;
        init
        {
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(value, TimeSpan.Zero);
            _window = value;
        }
    }

    /// <summary>
    /// The largest burst admitted at once, for the bucket algorithms. Defaults to
    /// <see cref="Limit"/> when not set.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is set and not positive.</exception>
    /// <remarks>
    /// <para>
    /// Only <see cref="RateLimitAlgorithm.TokenBucket"/> and
    /// <see cref="RateLimitAlgorithm.LeakyBucket"/> read this. A bucket has two independent
    /// parameters — how fast it refills and how much it holds — and collapsing them loses the
    /// property that makes a token bucket worth choosing. Leaving it unset gives capacity
    /// equal to the per-window allowance, which is the behaviour most rule authors expect.
    /// </para>
    /// <para>
    /// It is deliberately ignored by the window algorithms rather than rejected for them. A
    /// rule that sets a burst capacity and then switches to a fixed window should degrade to
    /// the nearest sensible behaviour, not throw at load time and take the whole rules file
    /// down with it. The rule validator reports it as a warning instead, which is the right
    /// severity: it is a sign of a mistake, but not one worth failing closed over.
    /// </para>
    /// </remarks>
    public int? BurstCapacity
    {
        get => _burstCapacity;
        init
        {
            if (value is not null)
            {
                ArgumentOutOfRangeException.ThrowIfLessThan(value.Value, 1);
            }

            _burstCapacity = value;
        }
    }

    /// <summary>
    /// The burst allowance actually in force: <see cref="BurstCapacity"/> when set, otherwise
    /// <see cref="Limit"/>.
    /// </summary>
    public int EffectiveCapacity => _burstCapacity ?? _limit;

    /// <summary>
    /// The sustained refill rate in permits per second.
    /// </summary>
    /// <remarks>
    /// Computed rather than configured, so it cannot contradict <see cref="Limit"/> and
    /// <see cref="Window"/>. Expressed as a <see cref="double"/> because the useful rates are
    /// routinely fractional — 5 permits per 10 seconds is 0.5/s — and rounding that to an
    /// integer would silently double or destroy the allowance.
    /// </remarks>
    public double PermitsPerSecond => _limit / _window.TotalSeconds;

    /// <summary>
    /// Returns a short human-readable description, used in demo output and validation
    /// messages.
    /// </summary>
    /// <returns>A string such as <c>TokenBucket 5/00:00:10 burst 10</c>.</returns>
    public override string ToString()
    {
        string burst = _burstCapacity is null ? string.Empty : $" burst {_burstCapacity}";
        return $"{Algorithm} {_limit}/{_window}{burst}";
    }
}
