using System.Collections.Immutable;

namespace RateLimiter.Core.Rules;

/// <summary>
/// One rule: which requests it applies to, how they are grouped, and what limit they face.
/// </summary>
/// <remarks>
/// The three parts are independent and all three matter. <see cref="Match"/> decides
/// <em>whether</em> the rule applies; <see cref="KeyBy"/> decides <em>who is counted together</em>
/// once it does; <see cref="Policy"/> decides <em>how much</em> they get. Conflating match and
/// key is a common mistake and produces a limiter that applies the right number to the wrong
/// population — for example one shared bucket for every free-tier caller rather than one bucket
/// each.
/// </remarks>
public sealed record RateLimitRule
{
    /// <summary>
    /// A stable, unique name, reported on every decision this rule makes.
    /// </summary>
    /// <remarks>
    /// Also forms the first segment of the limiter key, which is what keeps two rules that group
    /// by the same dimension from sharing a counter.
    /// </remarks>
    public required string Name { get; init; }

    /// <summary>
    /// Evaluation order. Lower numbers are considered first, and the first match wins.
    /// </summary>
    /// <remarks>
    /// An explicit integer rather than file order, because a rules file is edited by people and
    /// "why did this stop working" should not have the answer "someone inserted a line above it".
    /// </remarks>
    public int Priority { get; init; }

    /// <summary>
    /// The requests this rule applies to. Defaults to every request.
    /// </summary>
    public RuleMatch Match { get; init; } = RuleMatch.Any;

    /// <summary>
    /// The limit in force for matching requests.
    /// </summary>
    public required RateLimitPolicy Policy { get; init; }

    /// <summary>
    /// The dimensions whose values identify one counted population.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Keying by <c>clientId</c> gives each caller its own allowance. Keying by
    /// <c>clientId</c> and <c>endpoint</c> gives each caller a separate allowance per route.
    /// Keying by nothing at all produces a single shared allowance across every matching
    /// request, which is how a genuine global limit is expressed.
    /// </para>
    /// <para>
    /// A dimension named here but absent from a request falls back to a placeholder rather than
    /// failing the request, so that a rule keyed on an optional dimension degrades to grouping
    /// those callers together instead of erroring on every one of them.
    /// </para>
    /// </remarks>
    public ImmutableArray<string> KeyBy { get; init; } = [RequestFields.ClientId];

    /// <summary>
    /// Renders the rule for diagnostics and demo output.
    /// </summary>
    /// <returns>A string such as <c>free-search [p10] where tier in (free) key(clientId) TokenBucket 5/00:00:10</c>.</returns>
    public override string ToString() =>
        $"{Name} [p{Priority}] where {Match} key({string.Join('+', KeyBy)}) {Policy}";
}
