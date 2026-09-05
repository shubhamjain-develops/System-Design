using System.Collections.Immutable;
using RateLimiter.Core;
using RateLimiter.Core.Rules;

namespace RateLimiter.Demo;

/// <summary>
/// Serves a rule set whose algorithm can be swapped at runtime.
/// </summary>
/// <remarks>
/// <para>
/// The demo uses this to force every rule onto one algorithm on a keypress, so the same traffic
/// can be watched under all five. That is the claim from the design made visible: the algorithm
/// is a property of a rule, and changing it changes nothing else — not the key, not the
/// entitlement, not a line of engine code.
/// </para>
/// <para>
/// It also stands in for what a reload does. The engine reads <see cref="Current"/> once per
/// request and gets an immutable snapshot, so swapping the whole rule set between requests is
/// exactly what a rules-file reload does, minus the file.
/// </para>
/// </remarks>
internal sealed class OverridableRuleSource : IRuleSource
{
    // An explicit field rather than a captured primary-constructor parameter: capturing the
    // parameter AND using it to initialise another field is ambiguous enough that the compiler
    // rejects it (CS9124), and rightly — the two uses would read the same and mean different
    // things.
    private readonly RuleSet _original;
    private RuleSet _current;

    public OverridableRuleSource(RuleSet original)
    {
        _original = original;
        _current = original;
    }

    public RuleSet Current => Volatile.Read(ref _current);

    /// <summary>
    /// The algorithm currently forced onto every rule, or null when the file's own choices are
    /// in force.
    /// </summary>
    public RateLimitAlgorithm? Override { get; private set; }

    /// <summary>
    /// Cycles through "as configured" and each of the five algorithms.
    /// </summary>
    public void CycleAlgorithm()
    {
        RateLimitAlgorithm[] all = Enum.GetValues<RateLimitAlgorithm>();

        Override = Override is null
            ? all[0]
            : (int)Override.Value + 1 < all.Length ? all[(int)Override.Value + 1] : null;

        Volatile.Write(ref _current, Build());
    }

    private RuleSet Build()
    {
        if (Override is not { } algorithm)
        {
            return _original;
        }

        ImmutableArray<RateLimitRule> rewritten =
        [
            .. _original.Rules.Select(rule => rule with
            {
                Policy = rule.Policy with
                {
                    Algorithm = algorithm,

                    // Burst capacity is meaningless outside the bucket algorithms and would
                    // raise a validation warning on every switch, so it is dropped rather than
                    // carried across.
                    BurstCapacity = algorithm is RateLimitAlgorithm.TokenBucket or RateLimitAlgorithm.LeakyBucket
                        ? rule.Policy.BurstCapacity
                        : null,
                },
            }),
        ];

        return RuleSet.Create(rewritten);
    }
}
