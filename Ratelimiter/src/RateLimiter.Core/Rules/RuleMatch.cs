using System.Collections.Immutable;

namespace RateLimiter.Core.Rules;

/// <summary>
/// The conditions a request must satisfy for a rule to apply.
/// </summary>
/// <remarks>
/// <para>
/// A match is a set of conditions, one per dimension, combined with AND. Within a single
/// dimension the listed values are combined with OR. So
/// <c>tier in (free, trial) AND endpoint = /v1/search</c> is expressible, and that covers
/// essentially every real rate-limit rule.
/// </para>
/// <para>
/// What is deliberately not expressible is arbitrary boolean logic — no OR across dimensions,
/// no negation, no nesting. That restriction is what makes
/// <see cref="Covers"/> decidable, and <see cref="Covers"/> is what lets the rule set warn that
/// one rule has shadowed another. A rules language rich enough to express anything is also rich
/// enough that "is this rule reachable?" becomes undecidable, and an unreachable rule that
/// nobody warns you about is a limit you believe you have and do not.
/// </para>
/// <para>
/// A value of <c>*</c> matches any present value, and is how "this dimension must exist but may
/// be anything" is written.
/// </para>
/// </remarks>
public sealed record RuleMatch
{
    /// <summary>
    /// A match with no conditions, which applies to every request.
    /// </summary>
    /// <remarks>
    /// Used for the catch-all rule at the end of a rule set. A rule set without one leaves
    /// unmatched requests unlimited, which is sometimes intended and always worth being warned
    /// about.
    /// </remarks>
    public static readonly RuleMatch Any = new();

    /// <summary>
    /// The wildcard value, matching any present value for a dimension.
    /// </summary>
    public const string Wildcard = "*";

    /// <summary>
    /// Conditions keyed by dimension name, each holding the accepted values.
    /// </summary>
    public ImmutableDictionary<string, ImmutableArray<string>> Conditions { get; init; } =
        ImmutableDictionary<string, ImmutableArray<string>>.Empty;

    /// <summary>
    /// Builds a match from dimension/value pairs, accepting one value per dimension.
    /// </summary>
    /// <param name="conditions">The dimensions and their single accepted values.</param>
    /// <returns>The match.</returns>
    public static RuleMatch On(params (string Field, string Value)[] conditions)
    {
        ArgumentNullException.ThrowIfNull(conditions);

        ImmutableDictionary<string, ImmutableArray<string>>.Builder builder =
            ImmutableDictionary.CreateBuilder<string, ImmutableArray<string>>(StringComparer.OrdinalIgnoreCase);

        foreach ((string field, string value) in conditions)
        {
            builder[field] = [value];
        }

        return new RuleMatch { Conditions = builder.ToImmutable() };
    }

    /// <summary>
    /// Whether this match applies to a request.
    /// </summary>
    /// <param name="context">The request being evaluated.</param>
    /// <returns><see langword="true"/> when every condition is satisfied.</returns>
    /// <remarks>
    /// A dimension the request does not carry fails the condition rather than being ignored. The
    /// alternative — treating an absent dimension as a match — would let a caller escape a rule
    /// by omitting the header the rule keys on, which is the wrong direction for a control that
    /// exists to constrain callers.
    /// </remarks>
    public bool Matches(RequestContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        foreach (KeyValuePair<string, ImmutableArray<string>> condition in Conditions)
        {
            if (!context.TryGetValue(condition.Key, out string? actual))
            {
                return false;
            }

            if (!Accepts(condition.Value, actual))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Whether this match applies everywhere the other one does.
    /// </summary>
    /// <param name="other">The match suspected of being shadowed.</param>
    /// <returns>
    /// <see langword="true"/> when every request satisfying <paramref name="other"/> also
    /// satisfies this match, which means a rule carrying this match placed earlier makes the
    /// other rule unreachable.
    /// </returns>
    /// <remarks>
    /// The reasoning is per dimension. If this match constrains a dimension that the other does
    /// not constrain at all, the other is the broader one there and cannot be shadowed. If both
    /// constrain it, this one must accept everything the other accepts. Dimensions this match
    /// leaves unconstrained are free by definition.
    /// </remarks>
    public bool Covers(RuleMatch other)
    {
        ArgumentNullException.ThrowIfNull(other);

        foreach (KeyValuePair<string, ImmutableArray<string>> condition in Conditions)
        {
            if (!other.Conditions.TryGetValue(condition.Key, out ImmutableArray<string> otherValues))
            {
                return false;
            }

            if (condition.Value.Contains(Wildcard, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (string otherValue in otherValues)
            {
                if (!Accepts(condition.Value, otherValue))
                {
                    return false;
                }
            }
        }

        return true;
    }

    /// <summary>
    /// Renders the conditions for diagnostics and demo output.
    /// </summary>
    /// <returns>A string such as <c>tier in (free), endpoint in (/v1/search)</c>, or <c>*</c>.</returns>
    public override string ToString()
    {
        if (Conditions.IsEmpty)
        {
            return Wildcard;
        }

        return string.Join(
            ", ",
            Conditions.Select(c => $"{c.Key} in ({string.Join('|', c.Value)})"));
    }

    /// <summary>
    /// Compares two matches by their conditions.
    /// </summary>
    /// <param name="other">The match to compare with.</param>
    /// <returns><see langword="true"/> when both express the same conditions.</returns>
    /// <remarks>
    /// Written out rather than left to the compiler. A record's generated equality compares
    /// members with <see cref="object.Equals(object?)"/>, and an
    /// <see cref="ImmutableDictionary{TKey, TValue}"/> compares by reference — so two matches
    /// built from identical conditions would report themselves unequal. That would be a trap
    /// rather than an inefficiency: reload logic that asks "did the rules actually change?"
    /// would answer yes every time.
    ///
    /// Accepted values are compared as a set, because that is what they mean: a rule accepting
    /// free or trial is the same rule as one accepting trial or free.
    /// </remarks>
    public bool Equals(RuleMatch? other)
    {
        if (other is null)
        {
            return false;
        }

        if (ReferenceEquals(this, other))
        {
            return true;
        }

        if (Conditions.Count != other.Conditions.Count)
        {
            return false;
        }

        foreach (KeyValuePair<string, ImmutableArray<string>> condition in Conditions)
        {
            if (!other.Conditions.TryGetValue(condition.Key, out ImmutableArray<string> otherValues))
            {
                return false;
            }

            if (condition.Value.Length != otherValues.Length)
            {
                return false;
            }

            foreach (string value in condition.Value)
            {
                if (!otherValues.Contains(value, StringComparer.OrdinalIgnoreCase))
                {
                    return false;
                }
            }
        }

        return true;
    }

    /// <inheritdoc />
    public override int GetHashCode()
    {
        // Combined with XOR so the result does not depend on dictionary enumeration order,
        // which is not guaranteed to match between two equal instances.
        int hash = Conditions.Count;

        foreach (KeyValuePair<string, ImmutableArray<string>> condition in Conditions)
        {
            int entry = StringComparer.OrdinalIgnoreCase.GetHashCode(condition.Key);

            foreach (string value in condition.Value)
            {
                entry ^= StringComparer.OrdinalIgnoreCase.GetHashCode(value);
            }

            hash ^= entry;
        }

        return hash;
    }

    private static bool Accepts(ImmutableArray<string> accepted, string actual)
    {
        foreach (string value in accepted)
        {
            if (value.Equals(Wildcard, StringComparison.Ordinal)
                || value.Equals(actual, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
