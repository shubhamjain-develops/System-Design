using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;
using RateLimiter.Core.Rules;

namespace RateLimiter.Core.Configuration;

/// <summary>
/// The outcome of parsing a rules document.
/// </summary>
/// <param name="RuleSet">
/// The parsed rules, or <see langword="null"/> when the document could not be parsed at all.
/// </param>
/// <param name="Diagnostics">Everything found, from parse errors to shadowing warnings.</param>
public readonly record struct RuleParseResult(RuleSet? RuleSet, ImmutableArray<RuleDiagnostic> Diagnostics)
{
    /// <summary>
    /// Whether a usable rule set was produced.
    /// </summary>
    /// <remarks>
    /// Deliberately checks <see cref="Diagnostics"/> rather than only the rule set's own
    /// errors. A rule that fails to parse is skipped, so a document whose every rule was
    /// malformed would otherwise yield an empty-but-valid rule set and report success — which
    /// is the worst possible answer, because an empty rule set enforces nothing at all. Any
    /// error anywhere means the document was not understood, and a document that was not
    /// understood must not be allowed to replace the limits already in force.
    /// </remarks>
    public bool Succeeded =>
        RuleSet is not null
        && !RuleSet.HasErrors
        && !Diagnostics.Any(d => d.Severity == RuleDiagnosticSeverity.Error);
}

/// <summary>
/// Reads rules from JSON.
/// </summary>
/// <remarks>
/// <para>
/// JSON is the wire format the architecture assumes: a rule service publishes
/// <c>{limit, window, algorithm}</c> per route and limiter nodes cache it. Parsing it here means
/// the same document that would arrive over the network can be checked into a repository and
/// loaded from disk, so the demo and a deployment exercise the same code.
/// </para>
/// <para>
/// <strong>Parsing never throws.</strong> Every problem is returned as a diagnostic. That is not
/// squeamishness about exceptions — it is a consequence of where this input comes from. A rules
/// document is reloaded at runtime, so an exception would surface on a background thread with
/// nowhere useful to go, and the natural handling of it is to keep the previous rules. Making
/// the failure a value forces the caller to decide that deliberately.
/// </para>
/// <para>
/// Written by hand against <see cref="JsonDocument"/> rather than deserialised into DTOs. The
/// reason is error quality: a rules file is edited by a person under pressure, and
/// "rules[2].policy.limit must be a positive integer, saw 0" is worth more than a serializer's
/// type mismatch at a JSON path. It also keeps the wire shape from being dictated by C# property
/// names.
/// </para>
/// </remarks>
public static class JsonRuleParser
{
    /// <summary>
    /// Parses a rules document.
    /// </summary>
    /// <param name="json">The document.</param>
    /// <returns>The parsed rules, or the reasons they could not be parsed.</returns>
    public static RuleParseResult Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        ImmutableArray<RuleDiagnostic>.Builder diagnostics = ImmutableArray.CreateBuilder<RuleDiagnostic>();

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });
        }
        catch (JsonException ex)
        {
            diagnostics.Add(Error(string.Empty, $"The rules document is not valid JSON: {ex.Message}"));
            return new RuleParseResult(null, diagnostics.ToImmutable());
        }

        using (document)
        {
            if (!document.RootElement.TryGetProperty("rules", out JsonElement rulesElement)
                || rulesElement.ValueKind != JsonValueKind.Array)
            {
                diagnostics.Add(Error(string.Empty, "The document must have a 'rules' array at the root."));
                return new RuleParseResult(null, diagnostics.ToImmutable());
            }

            List<RateLimitRule> rules = [];
            int index = 0;

            foreach (JsonElement element in rulesElement.EnumerateArray())
            {
                string path = $"rules[{index}]";
                index++;

                if (TryReadRule(element, path, diagnostics, out RateLimitRule? rule))
                {
                    rules.Add(rule);
                }
            }

            // Even when some rules failed to parse, the ones that succeeded are validated and
            // returned. The caller can see both, and a single malformed rule does not make the
            // rest unreadable.
            RuleSet ruleSet = RuleSet.Create(rules);
            diagnostics.AddRange(ruleSet.Diagnostics);

            return new RuleParseResult(ruleSet, diagnostics.ToImmutable());
        }
    }

    private static bool TryReadRule(
        JsonElement element,
        string path,
        ImmutableArray<RuleDiagnostic>.Builder diagnostics,
        [NotNullWhen(true)] out RateLimitRule? rule)
    {
        rule = null;

        if (element.ValueKind != JsonValueKind.Object)
        {
            diagnostics.Add(Error(string.Empty, $"{path} must be an object."));
            return false;
        }

        if (!element.TryGetProperty("name", out JsonElement nameElement)
            || nameElement.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(nameElement.GetString()))
        {
            diagnostics.Add(Error(string.Empty, $"{path}.name is required and must be a non-empty string."));
            return false;
        }

        string name = nameElement.GetString()!;

        if (!element.TryGetProperty("policy", out JsonElement policyElement))
        {
            diagnostics.Add(Error(name, $"{path}.policy is required."));
            return false;
        }

        if (!TryReadPolicy(policyElement, $"{path}.policy", name, diagnostics, out RateLimitPolicy? policy))
        {
            return false;
        }

        int priority = 0;
        if (element.TryGetProperty("priority", out JsonElement priorityElement))
        {
            if (priorityElement.ValueKind != JsonValueKind.Number || !priorityElement.TryGetInt32(out priority))
            {
                diagnostics.Add(Error(name, $"{path}.priority must be an integer."));
                return false;
            }
        }

        if (!TryReadMatch(element, path, name, diagnostics, out RuleMatch? match))
        {
            return false;
        }

        if (!TryReadKeyBy(element, path, name, diagnostics, out ImmutableArray<string> keyBy))
        {
            return false;
        }

        rule = new RateLimitRule
        {
            Name = name,
            Priority = priority,
            Match = match,
            KeyBy = keyBy,
            Policy = policy,
        };

        return true;
    }

    private static bool TryReadPolicy(
        JsonElement element,
        string path,
        string ruleName,
        ImmutableArray<RuleDiagnostic>.Builder diagnostics,
        [NotNullWhen(true)] out RateLimitPolicy? policy)
    {
        policy = null;

        if (element.ValueKind != JsonValueKind.Object)
        {
            diagnostics.Add(Error(ruleName, $"{path} must be an object."));
            return false;
        }

        if (!element.TryGetProperty("algorithm", out JsonElement algorithmElement)
            || algorithmElement.ValueKind != JsonValueKind.String
            || !TryParseAlgorithm(algorithmElement.GetString(), out RateLimitAlgorithm algorithm))
        {
            diagnostics.Add(Error(
                ruleName,
                $"{path}.algorithm is required and must be one of: {string.Join(", ", Enum.GetNames<RateLimitAlgorithm>())}."));
            return false;
        }

        if (!element.TryGetProperty("limit", out JsonElement limitElement)
            || limitElement.ValueKind != JsonValueKind.Number
            || !limitElement.TryGetInt32(out int limit)
            || limit < 1)
        {
            diagnostics.Add(Error(ruleName, $"{path}.limit is required and must be a positive integer."));
            return false;
        }

        if (!element.TryGetProperty("windowSeconds", out JsonElement windowElement)
            || windowElement.ValueKind != JsonValueKind.Number
            || !windowElement.TryGetDouble(out double windowSeconds)
            || windowSeconds <= 0)
        {
            diagnostics.Add(Error(ruleName, $"{path}.windowSeconds is required and must be a positive number."));
            return false;
        }

        int? burstCapacity = null;
        if (element.TryGetProperty("burstCapacity", out JsonElement burstElement)
            && burstElement.ValueKind != JsonValueKind.Null)
        {
            if (burstElement.ValueKind != JsonValueKind.Number
                || !burstElement.TryGetInt32(out int burst)
                || burst < 1)
            {
                diagnostics.Add(Error(ruleName, $"{path}.burstCapacity must be a positive integer when present."));
                return false;
            }

            burstCapacity = burst;
        }

        policy = new RateLimitPolicy
        {
            Algorithm = algorithm,
            Limit = limit,
            Window = TimeSpan.FromSeconds(windowSeconds),
            BurstCapacity = burstCapacity,
        };

        return true;
    }

    private static bool TryReadMatch(
        JsonElement element,
        string path,
        string ruleName,
        ImmutableArray<RuleDiagnostic>.Builder diagnostics,
        out RuleMatch match)
    {
        match = RuleMatch.Any;

        if (!element.TryGetProperty("match", out JsonElement matchElement)
            || matchElement.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        if (matchElement.ValueKind != JsonValueKind.Object)
        {
            diagnostics.Add(Error(ruleName, $"{path}.match must be an object of dimension/value pairs."));
            return false;
        }

        ImmutableDictionary<string, ImmutableArray<string>>.Builder conditions =
            ImmutableDictionary.CreateBuilder<string, ImmutableArray<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (JsonProperty property in matchElement.EnumerateObject())
        {
            // A single value and a list of values are both accepted, because requiring
            // ["free"] for the overwhelmingly common one-value case is friction that produces
            // no benefit.
            if (property.Value.ValueKind == JsonValueKind.String)
            {
                conditions[property.Name] = [property.Value.GetString()!];
                continue;
            }

            if (property.Value.ValueKind == JsonValueKind.Array)
            {
                List<string> values = [];

                foreach (JsonElement item in property.Value.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.String)
                    {
                        diagnostics.Add(Error(
                            ruleName,
                            $"{path}.match.{property.Name} must contain only strings."));
                        return false;
                    }

                    values.Add(item.GetString()!);
                }

                if (values.Count == 0)
                {
                    diagnostics.Add(Error(
                        ruleName,
                        $"{path}.match.{property.Name} is an empty list, which can never match. Remove the condition to match any value."));
                    return false;
                }

                conditions[property.Name] = [.. values];
                continue;
            }

            diagnostics.Add(Error(
                ruleName,
                $"{path}.match.{property.Name} must be a string or a list of strings."));
            return false;
        }

        match = new RuleMatch { Conditions = conditions.ToImmutable() };
        return true;
    }

    private static bool TryReadKeyBy(
        JsonElement element,
        string path,
        string ruleName,
        ImmutableArray<RuleDiagnostic>.Builder diagnostics,
        out ImmutableArray<string> keyBy)
    {
        keyBy = [RequestFields.ClientId];

        if (!element.TryGetProperty("keyBy", out JsonElement keyByElement)
            || keyByElement.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        if (keyByElement.ValueKind != JsonValueKind.Array)
        {
            diagnostics.Add(Error(ruleName, $"{path}.keyBy must be a list of dimension names."));
            return false;
        }

        List<string> fields = [];

        foreach (JsonElement item in keyByElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(item.GetString()))
            {
                diagnostics.Add(Error(ruleName, $"{path}.keyBy must contain only non-empty strings."));
                return false;
            }

            fields.Add(item.GetString()!);
        }

        // An explicitly empty list is meaningful: one allowance shared across every matching
        // request. It is preserved rather than defaulted, because silently substituting a
        // per-client key would turn a global limit into no limit at all.
        keyBy = [.. fields];
        return true;
    }

    private static bool TryParseAlgorithm(string? value, out RateLimitAlgorithm algorithm)
    {
        algorithm = default;

        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        // Accepts tokenBucket, token_bucket, token-bucket and TokenBucket alike. A rules file is
        // written by hand and rejecting it over a separator would be pedantry with a real cost:
        // the file fails to load and the previous limits stay in force unnoticed.
        string normalised = string.Concat(value.Where(char.IsLetterOrDigit));

        return Enum.TryParse(normalised, ignoreCase: true, out algorithm)
            && Enum.IsDefined(algorithm);
    }

    private static RuleDiagnostic Error(string ruleName, string message) =>
        new(RuleDiagnosticSeverity.Error, ruleName, string.Create(CultureInfo.InvariantCulture, $"{message}"));
}
