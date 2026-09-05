namespace RateLimiter.Core.Rules;

/// <summary>
/// Supplies the rule set currently in force.
/// </summary>
/// <remarks>
/// <para>
/// The engine reads <see cref="Current"/> once per request, so an implementation must make that
/// cheap — returning a cached immutable snapshot rather than parsing, locking, or calling out to
/// anything. This is the seam that keeps the rule engine off the hot path: rules may originate
/// from a remote service and change at any time, but a request never waits on that.
/// </para>
/// <para>
/// Returning an immutable snapshot rather than a live collection also means a request evaluated
/// during a reload sees one coherent version of the rules, not a half-applied mixture of the old
/// and new set.
/// </para>
/// </remarks>
public interface IRuleSource
{
    /// <summary>
    /// The rule set in force right now.
    /// </summary>
    RuleSet Current { get; }
}

/// <summary>
/// An <see cref="IRuleSource"/> holding one fixed rule set.
/// </summary>
/// <remarks>
/// The right choice for tests and for a deployment whose rules ship with the binary. Anything
/// that reloads belongs behind the same interface, so swapping one for the other changes no
/// calling code.
/// </remarks>
public sealed class StaticRuleSource : IRuleSource
{
    /// <summary>
    /// Creates a source over a fixed rule set.
    /// </summary>
    /// <param name="ruleSet">The rules in force.</param>
    public StaticRuleSource(RuleSet ruleSet)
    {
        ArgumentNullException.ThrowIfNull(ruleSet);
        Current = ruleSet;
    }

    /// <summary>
    /// Creates a source over a fixed collection of rules, validating them.
    /// </summary>
    /// <param name="rules">The rules in force.</param>
    public StaticRuleSource(params RateLimitRule[] rules)
        : this(RuleSet.Create(rules))
    {
    }

    /// <inheritdoc />
    public RuleSet Current { get; }
}
