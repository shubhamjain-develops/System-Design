using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;

namespace RateLimiter.Core.Rules;

/// <summary>
/// How serious a rule-set problem is.
/// </summary>
public enum RuleDiagnosticSeverity
{
    /// <summary>Worth knowing, but the rule set is usable.</summary>
    Information = 0,

    /// <summary>Very likely a mistake, but the rule set still loads.</summary>
    Warning = 1,

    /// <summary>The rule set cannot be used.</summary>
    Error = 2,
}

/// <summary>
/// One problem found while validating a rule set.
/// </summary>
/// <param name="Severity">How serious it is.</param>
/// <param name="RuleName">The rule concerned, or empty when it concerns the set as a whole.</param>
/// <param name="Message">A description naming what to change.</param>
public readonly record struct RuleDiagnostic(
    RuleDiagnosticSeverity Severity,
    string RuleName,
    string Message)
{
    /// <summary>Renders the diagnostic for logs and demo output.</summary>
    /// <returns>A string such as <c>Warning [free-search]: shadowed by 'catch-all'.</c></returns>
    public override string ToString() =>
        string.IsNullOrEmpty(RuleName) ? $"{Severity}: {Message}" : $"{Severity} [{RuleName}]: {Message}";
}

/// <summary>
/// An ordered, validated collection of rules.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Matching is first-match-wins over priority order.</strong> The alternative is to
/// apply every matching rule and require the request to satisfy all of them, which is what
/// Envoy's descriptor model does. That is genuinely safer — you cannot widen a limit by adding
/// a rule — and it was rejected for one reason: it makes "why was this request rejected?" a
/// multi-rule answer, where first-match makes it a single rule name that fits in a log line and
/// on the demo screen. For a library whose purpose is to be understood, explicability won.
/// </para>
/// <para>
/// The cost of that choice is that a broad rule placed early silently disables the narrower
/// rules beneath it. Since that is the failure mode the rejected design would have prevented,
/// paying for it is not optional: <see cref="Create"/> detects shadowed rules and reports them
/// by name. A limit you believe you have and do not is the worst outcome available here.
/// </para>
/// </remarks>
public sealed class RuleSet
{
    private RuleSet(ImmutableArray<RateLimitRule> ordered, ImmutableArray<RuleDiagnostic> diagnostics)
    {
        Rules = ordered;
        Diagnostics = diagnostics;
    }

    /// <summary>
    /// An empty rule set, which limits nothing.
    /// </summary>
    public static RuleSet Empty { get; } = new([], []);

    /// <summary>
    /// The rules in evaluation order: ascending priority, then declaration order.
    /// </summary>
    public ImmutableArray<RateLimitRule> Rules { get; }

    /// <summary>
    /// Problems found during validation.
    /// </summary>
    public ImmutableArray<RuleDiagnostic> Diagnostics { get; }

    /// <summary>
    /// Whether any diagnostic is an error.
    /// </summary>
    public bool HasErrors => Diagnostics.Any(d => d.Severity == RuleDiagnosticSeverity.Error);

    /// <summary>
    /// Orders and validates a collection of rules.
    /// </summary>
    /// <param name="rules">The rules, in any order.</param>
    /// <returns>The rule set, including any diagnostics.</returns>
    /// <remarks>
    /// Validation never throws. A rules file arriving from outside the process — which is the
    /// design this library assumes — must be able to report what is wrong with it, and a
    /// constructor that throws on the first problem reports exactly one. The caller decides
    /// whether errors are fatal.
    /// </remarks>
    public static RuleSet Create(IEnumerable<RateLimitRule> rules)
    {
        ArgumentNullException.ThrowIfNull(rules);

        ImmutableArray<RateLimitRule> ordered =
        [
            .. rules
                .Select((rule, index) => (rule, index))
                .OrderBy(x => x.rule.Priority)
                .ThenBy(x => x.index)
                .Select(x => x.rule),
        ];

        ImmutableArray<RuleDiagnostic>.Builder diagnostics = ImmutableArray.CreateBuilder<RuleDiagnostic>();

        DetectDuplicateNames(ordered, diagnostics);
        DetectShadowedRules(ordered, diagnostics);
        DetectIgnoredBurstCapacity(ordered, diagnostics);
        DetectMissingCatchAll(ordered, diagnostics);

        return new RuleSet(ordered, diagnostics.ToImmutable());
    }

    /// <summary>
    /// Finds the first rule that applies to a request.
    /// </summary>
    /// <param name="context">The request.</param>
    /// <param name="rule">The matching rule, when one is found.</param>
    /// <returns><see langword="true"/> when a rule matched.</returns>
    public bool TryMatch(RequestContext context, [NotNullWhen(true)] out RateLimitRule? rule)
    {
        ArgumentNullException.ThrowIfNull(context);

        foreach (RateLimitRule candidate in Rules)
        {
            if (candidate.Match.Matches(context))
            {
                rule = candidate;
                return true;
            }
        }

        rule = null;
        return false;
    }

    private static void DetectDuplicateNames(
        ImmutableArray<RateLimitRule> ordered,
        ImmutableArray<RuleDiagnostic>.Builder diagnostics)
    {
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);

        foreach (RateLimitRule rule in ordered)
        {
            if (string.IsNullOrWhiteSpace(rule.Name))
            {
                diagnostics.Add(new RuleDiagnostic(
                    RuleDiagnosticSeverity.Error,
                    string.Empty,
                    "A rule has no name. Names identify a rule on every decision it makes and form part of its storage key."));
                continue;
            }

            if (!seen.Add(rule.Name))
            {
                // Duplicate names are an error rather than a warning because the name is part of
                // the limiter key: two rules sharing a name share a counter, so each silently
                // consumes the other's allowance.
                diagnostics.Add(new RuleDiagnostic(
                    RuleDiagnosticSeverity.Error,
                    rule.Name,
                    "Duplicate rule name. Rule names form part of the storage key, so two rules sharing a name would share a counter."));
            }
        }
    }

    private static void DetectShadowedRules(
        ImmutableArray<RateLimitRule> ordered,
        ImmutableArray<RuleDiagnostic>.Builder diagnostics)
    {
        for (int i = 0; i < ordered.Length; i++)
        {
            for (int j = 0; j < i; j++)
            {
                if (!ordered[j].Match.Covers(ordered[i].Match))
                {
                    continue;
                }

                diagnostics.Add(new RuleDiagnostic(
                    RuleDiagnosticSeverity.Warning,
                    ordered[i].Name,
                    $"Unreachable: every request it matches is already matched by the earlier rule '{ordered[j].Name}' "
                    + $"(priority {ordered[j].Priority} vs {ordered[i].Priority}). Give it a lower priority number, or narrow the earlier rule."));

                // One report per shadowed rule. Naming the first shadower is enough to act on,
                // and listing every one of them buries the finding.
                break;
            }
        }
    }

    private static void DetectIgnoredBurstCapacity(
        ImmutableArray<RateLimitRule> ordered,
        ImmutableArray<RuleDiagnostic>.Builder diagnostics)
    {
        foreach (RateLimitRule rule in ordered)
        {
            bool bucketAlgorithm = rule.Policy.Algorithm
                is RateLimitAlgorithm.TokenBucket
                or RateLimitAlgorithm.LeakyBucket;

            if (rule.Policy.BurstCapacity is not null && !bucketAlgorithm)
            {
                // A warning rather than an error, deliberately. Failing the whole rules file
                // over an ignored field would take every other rule down with it, and the
                // request this rule governs would then be limited by something else entirely.
                diagnostics.Add(new RuleDiagnostic(
                    RuleDiagnosticSeverity.Warning,
                    rule.Name,
                    $"burstCapacity is set but '{rule.Policy.Algorithm}' ignores it. Only the bucket algorithms have a separate burst allowance."));
            }
        }
    }

    private static void DetectMissingCatchAll(
        ImmutableArray<RateLimitRule> ordered,
        ImmutableArray<RuleDiagnostic>.Builder diagnostics)
    {
        if (ordered.IsEmpty || ordered.Any(r => r.Match.Conditions.IsEmpty))
        {
            return;
        }

        diagnostics.Add(new RuleDiagnostic(
            RuleDiagnosticSeverity.Information,
            string.Empty,
            "No catch-all rule. Requests matching no rule are not limited at all, which is often intended but is worth stating deliberately."));
    }
}
