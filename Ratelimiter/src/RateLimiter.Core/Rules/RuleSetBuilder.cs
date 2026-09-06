using System.Collections.Immutable;

namespace RateLimiter.Core.Rules;

/// <summary>
/// Builds one rule in code.
/// </summary>
/// <remarks>
/// The code path and the JSON path construct the same immutable <see cref="RateLimitRule"/>.
/// That is the point of having both: configuration is treated as a <em>source</em> of rules
/// rather than as the rule model itself, so there is exactly one set of semantics to understand
/// and to test. Tests use this builder and touch no files; deployments use JSON and change no
/// code; neither can drift from the other because there is nothing between them to drift.
/// </remarks>
public sealed class RuleBuilder
{
    private readonly string _name;
    private readonly Dictionary<string, ImmutableArray<string>> _conditions =
        new(StringComparer.OrdinalIgnoreCase);

    private int _priority;
    private ImmutableArray<string> _keyBy = [RequestFields.ClientId];
    private RateLimitPolicy? _policy;

    internal RuleBuilder(string name)
    {
        _name = name;
    }

    /// <summary>
    /// Sets evaluation order. Lower numbers are considered first.
    /// </summary>
    /// <param name="priority">The priority.</param>
    /// <returns>This builder.</returns>
    public RuleBuilder WithPriority(int priority)
    {
        _priority = priority;
        return this;
    }

    /// <summary>
    /// Adds a condition. Repeating a dimension replaces its accepted values.
    /// </summary>
    /// <param name="field">The dimension name, from <see cref="RequestFields"/> or custom.</param>
    /// <param name="values">The accepted values. <see cref="RuleMatch.Wildcard"/> accepts any.</param>
    /// <returns>This builder.</returns>
    public RuleBuilder Matching(string field, params string[] values)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(field);
        ArgumentNullException.ThrowIfNull(values);

        _conditions[field] = [.. values];
        return this;
    }

    /// <summary>
    /// Sets the dimensions that identify one counted population.
    /// </summary>
    /// <param name="fields">
    /// The dimensions. Passing none produces a single allowance shared by every matching
    /// request, which is how a genuine global limit is expressed.
    /// </param>
    /// <returns>This builder.</returns>
    public RuleBuilder KeyedBy(params string[] fields)
    {
        ArgumentNullException.ThrowIfNull(fields);

        _keyBy = [.. fields];
        return this;
    }

    /// <summary>
    /// Sets the policy.
    /// </summary>
    /// <param name="algorithm">The algorithm.</param>
    /// <param name="limit">Permits per window.</param>
    /// <param name="window">The window.</param>
    /// <param name="burstCapacity">Burst allowance, for the bucket algorithms.</param>
    /// <returns>This builder.</returns>
    public RuleBuilder Using(
        RateLimitAlgorithm algorithm,
        int limit,
        TimeSpan window,
        int? burstCapacity = null)
    {
        _policy = new RateLimitPolicy
        {
            Algorithm = algorithm,
            Limit = limit,
            Window = window,
            BurstCapacity = burstCapacity,
        };

        return this;
    }

    internal RateLimitRule Build()
    {
        if (_policy is null)
        {
            throw new InvalidOperationException(
                $"Rule '{_name}' has no policy. Call {nameof(Using)} to set one.");
        }

        return new RateLimitRule
        {
            Name = _name,
            Priority = _priority,
            Match = _conditions.Count == 0
                ? RuleMatch.Any
                : new RuleMatch
                {
                    Conditions = _conditions.ToImmutableDictionary(StringComparer.OrdinalIgnoreCase),
                },
            KeyBy = _keyBy,
            Policy = _policy,
        };
    }
}

/// <summary>
/// Builds a rule set in code.
/// </summary>
public sealed class RuleSetBuilder
{
    private readonly List<RuleBuilder> _rules = [];

    /// <summary>
    /// Creates a builder.
    /// </summary>
    /// <returns>The builder.</returns>
    public static RuleSetBuilder Create() => new();

    /// <summary>
    /// Adds a rule.
    /// </summary>
    /// <param name="name">The rule's unique name.</param>
    /// <param name="configure">Configures the rule.</param>
    /// <returns>This builder.</returns>
    public RuleSetBuilder Add(string name, Action<RuleBuilder> configure)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(configure);

        RuleBuilder builder = new(name);
        configure(builder);
        _rules.Add(builder);

        return this;
    }

    /// <summary>
    /// Orders and validates the rules.
    /// </summary>
    /// <returns>The rule set, including any diagnostics.</returns>
    public RuleSet Build() => RuleSet.Create(_rules.Select(r => r.Build()));
}
