namespace RateLimiter.Core;

/// <summary>
/// The outcome of evaluating one request, and the reasoning behind it.
/// </summary>
/// <remarks>
/// <para>
/// A boolean would be enough to enforce a limit. It is not enough to operate one, and the
/// difference is the whole of this type.
/// </para>
/// <para>
/// The question asked of a rate limiter in production is never "did it reject?" — that is
/// visible from the 429. It is "why was <em>this</em> caller rejected when the limit looks
/// generous?", and answering it requires knowing which rule matched, which is exactly the
/// fact a boolean discards. Carrying <see cref="RuleName"/> and <see cref="LimiterKey"/> on
/// every decision, allowed ones included, turns that from an investigation into a log line.
/// </para>
/// <para>
/// <see cref="StoreFailureOccurred"/> is here for the failure mode that is otherwise
/// invisible. When the backing store is unreachable and the configured policy is to fail
/// open, the limiter stops limiting and every response still says 200. Without a flag on the
/// decision itself, the only symptom is a bill or an outage. A limiter that has stopped
/// limiting must be able to say so.
/// </para>
/// </remarks>
public sealed record RateLimitDecision
{
    /// <summary>
    /// Whether the request may proceed.
    /// </summary>
    public required bool IsAllowed { get; init; }

    /// <summary>
    /// The name of the rule that decided this request.
    /// </summary>
    /// <remarks>
    /// Exactly one rule decides any request, because matching is first-match-wins. That is
    /// what lets this be a single name rather than a list, and a single name is what makes
    /// the decision explainable in one line of log output.
    /// </remarks>
    public required string RuleName { get; init; }

    /// <summary>
    /// The algorithm that produced this decision.
    /// </summary>
    /// <remarks>
    /// Carried separately from the rule name because the interesting question during a
    /// configuration change is whether dispatch actually followed the rules file. If every
    /// decision reports the same algorithm after a rule was edited to change it, the rules
    /// were not reloaded.
    /// </remarks>
    public required RateLimitAlgorithm Algorithm { get; init; }

    /// <summary>
    /// The storage key this decision was accounted against.
    /// </summary>
    /// <remarks>
    /// Includes the rule name as its first segment, so two rules keyed on the same client do
    /// not share a counter. Exposing it makes that property observable in a test and in the
    /// demo rather than merely intended.
    /// </remarks>
    public required string LimiterKey { get; init; }

    /// <summary>
    /// Permits still available under this policy at the moment of the decision.
    /// </summary>
    /// <remarks>
    /// Advisory rather than a guarantee. In a distributed deployment another node may spend a
    /// permit between this decision and the caller reading the value, so it is suitable for a
    /// response header and unsuitable as the basis for a client's own accounting.
    /// </remarks>
    public required long RemainingPermits { get; init; }

    /// <summary>
    /// How long the caller should wait before retrying, when known. Null for allowed requests.
    /// </summary>
    /// <remarks>
    /// This is the difference between a limiter that sheds load and one that concentrates it.
    /// A rejected client with no guidance retries immediately, so the moment a limit is hit
    /// the rejected traffic becomes the dominant traffic. Every algorithm here computes the
    /// instant its next permit becomes available rather than returning a fixed guess.
    /// </remarks>
    public TimeSpan? RetryAfter { get; init; }

    /// <summary>
    /// Whether the backing store failed during this evaluation and the configured failure
    /// policy was applied.
    /// </summary>
    /// <remarks>
    /// When this is <see langword="true"/>, <see cref="IsAllowed"/> reflects the failure
    /// policy and not the caller's actual consumption, and
    /// <see cref="RemainingPermits"/> is not meaningful.
    /// </remarks>
    public bool StoreFailureOccurred { get; init; }

    /// <summary>
    /// Returns a one-line description suitable for logs and demo output.
    /// </summary>
    /// <returns>A string such as <c>ALLOW  free-tier-search  TokenBucket  remaining=4</c>.</returns>
    public override string ToString()
    {
        string verdict = IsAllowed ? "ALLOW" : "REJECT";
        string degraded = StoreFailureOccurred ? " (store failure)" : string.Empty;
        string retry = RetryAfter is null ? string.Empty : $" retryAfter={RetryAfter.Value.TotalSeconds:0.###}s";
        return $"{verdict} {RuleName} {Algorithm} remaining={RemainingPermits}{retry}{degraded}";
    }
}
