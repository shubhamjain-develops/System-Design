using System.Text;

namespace RateLimiter.Core.Rules;

/// <summary>
/// Builds the storage key that identifies one counted population under one rule.
/// </summary>
/// <remarks>
/// <para>
/// Two properties matter here and both are security properties rather than conveniences.
/// </para>
/// <para>
/// <strong>The rule name is part of the key.</strong> Without it, two rules that both group by
/// client ID would share a counter, and a caller subject to a 100/hour rule and a 10/minute rule
/// would find each one consuming the other's allowance — silently halving both. Nothing in the
/// output would indicate it.
/// </para>
/// <para>
/// <strong>Values are escaped.</strong> Dimension values are caller-supplied. If a client can
/// choose an ID containing the delimiter, it can craft one whose key collides with another
/// client's — spending their quota, or hiding inside it. Escaping the delimiters and the escape
/// character makes the encoding unambiguous, so distinct inputs cannot produce identical keys.
/// </para>
/// </remarks>
public static class LimiterKey
{
    private const char FieldSeparator = '|';
    private const char ValueSeparator = '=';
    private const char EscapeChar = '\\';

    /// <summary>
    /// Tags a dimension the request carried. The escaped value follows it.
    /// </summary>
    private const char PresentTag = 'v';

    /// <summary>
    /// Tags a dimension the request did not carry. Nothing follows it.
    /// </summary>
    /// <remarks>
    /// A tag character rather than a reserved marker string, because any reserved string is
    /// forgeable: a caller who learns the marker can send it as their own client ID and join the
    /// population of callers who omitted that dimension. Since every present value is preceded
    /// by <see cref="PresentTag"/>, no caller-supplied input can produce the absent encoding.
    /// </remarks>
    private const char AbsentTag = '-';

    /// <summary>
    /// Builds the key for a request under a rule.
    /// </summary>
    /// <param name="rule">The rule that matched.</param>
    /// <param name="context">The request.</param>
    /// <returns>An unambiguous key, stable for the same rule and dimension values.</returns>
    public static string Build(RateLimitRule rule, RequestContext context)
    {
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentNullException.ThrowIfNull(context);

        StringBuilder builder = new();
        AppendEscaped(builder, rule.Name);

        foreach (string field in rule.KeyBy)
        {
            builder.Append(FieldSeparator);
            AppendEscaped(builder, field);
            builder.Append(ValueSeparator);

            if (context.TryGetValue(field, out string? value))
            {
                builder.Append(PresentTag);
                AppendEscaped(builder, value);
            }
            else
            {
                builder.Append(AbsentTag);
            }
        }

        return builder.ToString();
    }

    private static void AppendEscaped(StringBuilder builder, string value)
    {
        foreach (char c in value)
        {
            if (c is EscapeChar or FieldSeparator or ValueSeparator)
            {
                builder.Append(EscapeChar);
            }

            builder.Append(c);
        }
    }
}
