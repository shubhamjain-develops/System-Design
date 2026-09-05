using System.Diagnostics.CodeAnalysis;

namespace RateLimiter.Core;

/// <summary>
/// The facts about an incoming request that rules match on and limiter keys are built from.
/// </summary>
/// <remarks>
/// <para>
/// The shape here is a deliberate compromise between two designs that both have real
/// advocates.
/// </para>
/// <para>
/// A purely typed context — fixed properties and nothing else — is discoverable and
/// compiler-checked, but every new dimension is a breaking change to the type, and a rules
/// file cannot introduce one at all. A purely untyped context — a bare dictionary of
/// descriptors, which is what Envoy's rate-limit service uses — accepts any dimension a
/// deployment invents, at the cost that nothing is discoverable and every field name is a
/// string that might be misspelled.
/// </para>
/// <para>
/// This type takes the middle path: the dimensions that nearly every deployment needs are
/// real properties with real types, and <see cref="Attributes"/> carries anything else. Both
/// are reachable through <see cref="TryGetValue"/> under one vocabulary, which matters more
/// than it first appears — it means the JSON rules file, the rule matcher, and the key
/// builder all name a dimension the same way, so there is no translation layer between
/// configuration and runtime that could drift out of step.
/// </para>
/// </remarks>
public sealed record RequestContext
{
    /// <summary>
    /// Identifies the caller being limited: an API key, account ID, or user ID.
    /// </summary>
    /// <remarks>
    /// Required because a rate limiter with no notion of who is calling can only express a
    /// global limit, and a global limit is a capacity control rather than a rate limit. Where
    /// a genuinely global limit is wanted, a rule keyed on nothing expresses it explicitly.
    /// </remarks>
    public required string ClientId { get; init; }

    /// <summary>
    /// The route or operation being called, such as <c>/v1/payments</c>.
    /// </summary>
    public string? Endpoint { get; init; }

    /// <summary>
    /// The caller's service tier, such as <c>free</c>, <c>pro</c>, or <c>enterprise</c>.
    /// </summary>
    public string? Tier { get; init; }

    /// <summary>
    /// The caller's IP address.
    /// </summary>
    /// <remarks>
    /// Worth limiting on when a caller is unauthenticated and therefore has no
    /// <see cref="ClientId"/> worth trusting, but weak on its own: many legitimate callers
    /// share an address behind NAT, and an attacker with an address pool has many.
    /// </remarks>
    public string? IpAddress { get; init; }

    /// <summary>
    /// The HTTP method, or an equivalent verb for non-HTTP transports.
    /// </summary>
    public string? Method { get; init; }

    /// <summary>
    /// Additional deployment-specific dimensions, resolved by name alongside the well-known
    /// properties above.
    /// </summary>
    public IReadOnlyDictionary<string, string>? Attributes { get; init; }

    /// <summary>
    /// Resolves a dimension by name, checking the well-known properties first and
    /// <see cref="Attributes"/> second.
    /// </summary>
    /// <param name="field">
    /// A dimension name. Use the constants on <see cref="RequestFields"/> for the well-known
    /// ones. Matching is case-insensitive so that a rules file written by a human is not
    /// rejected over <c>clientId</c> versus <c>ClientID</c>.
    /// </param>
    /// <param name="value">The resolved value, or <see langword="null"/> when absent.</param>
    /// <returns>
    /// <see langword="true"/> when the dimension resolved to a non-null, non-empty value.
    /// </returns>
    /// <remarks>
    /// An empty string is treated as absent rather than as a value. The alternative lets a
    /// caller that supplies <c>tier=""</c> land in a different limiter bucket from one that
    /// omits the tier entirely, which is a quota-evasion trick and not a distinction any rule
    /// author meant to draw.
    /// </remarks>
    public bool TryGetValue(string field, [NotNullWhen(true)] out string? value)
    {
        ArgumentNullException.ThrowIfNull(field);

        value = field switch
        {
            _ when field.Equals(RequestFields.ClientId, StringComparison.OrdinalIgnoreCase) => ClientId,
            _ when field.Equals(RequestFields.Endpoint, StringComparison.OrdinalIgnoreCase) => Endpoint,
            _ when field.Equals(RequestFields.Tier, StringComparison.OrdinalIgnoreCase) => Tier,
            _ when field.Equals(RequestFields.IpAddress, StringComparison.OrdinalIgnoreCase) => IpAddress,
            _ when field.Equals(RequestFields.Method, StringComparison.OrdinalIgnoreCase) => Method,
            _ => LookupAttribute(field),
        };

        if (string.IsNullOrEmpty(value))
        {
            value = null;
            return false;
        }

        return true;
    }

    private string? LookupAttribute(string field)
    {
        if (Attributes is null)
        {
            return null;
        }

        // A caller-supplied dictionary may use any comparer, including a case-sensitive one,
        // so an ordinal-ignore-case scan is the only way to keep resolution consistent with
        // the well-known fields above. Attribute counts are small (single digits in practice)
        // and this runs once per rule evaluation, so the linear scan is not worth optimising
        // away at the cost of that consistency.
        foreach (KeyValuePair<string, string> pair in Attributes)
        {
            if (pair.Key.Equals(field, StringComparison.OrdinalIgnoreCase))
            {
                return pair.Value;
            }
        }

        return null;
    }
}

/// <summary>
/// The names of the well-known dimensions on <see cref="RequestContext"/>.
/// </summary>
/// <remarks>
/// These constants are the shared vocabulary between the JSON rules schema and the runtime.
/// Referencing them rather than repeating string literals is what keeps a typo in the rule
/// matcher from becoming a rule that silently never matches.
/// </remarks>
public static class RequestFields
{
    /// <summary>Names <see cref="RequestContext.ClientId"/>.</summary>
    public const string ClientId = "clientId";

    /// <summary>Names <see cref="RequestContext.Endpoint"/>.</summary>
    public const string Endpoint = "endpoint";

    /// <summary>Names <see cref="RequestContext.Tier"/>.</summary>
    public const string Tier = "tier";

    /// <summary>Names <see cref="RequestContext.IpAddress"/>.</summary>
    public const string IpAddress = "ip";

    /// <summary>Names <see cref="RequestContext.Method"/>.</summary>
    public const string Method = "method";
}
