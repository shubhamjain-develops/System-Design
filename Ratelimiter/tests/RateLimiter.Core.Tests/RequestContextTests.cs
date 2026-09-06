namespace RateLimiter.Core.Tests;

/// <summary>
/// Covers dimension resolution, which both rule matching and limiter-key construction depend on.
/// </summary>
public sealed class RequestContextTests
{
    private static RequestContext Sample() => new()
    {
        ClientId = "acct-1",
        Endpoint = "/v1/search",
        Tier = "free",
        IpAddress = "203.0.113.9",
        Method = "GET",
    };

    [Theory]
    [InlineData(RequestFields.ClientId, "acct-1")]
    [InlineData(RequestFields.Endpoint, "/v1/search")]
    [InlineData(RequestFields.Tier, "free")]
    [InlineData(RequestFields.IpAddress, "203.0.113.9")]
    [InlineData(RequestFields.Method, "GET")]
    public void Resolves_every_well_known_field(string field, string expected)
    {
        Assert.True(Sample().TryGetValue(field, out string? value));
        Assert.Equal(expected, value);
    }

    [Theory]
    [InlineData("CLIENTID")]
    [InlineData("clientid")]
    [InlineData("ClientId")]
    public void Field_names_are_case_insensitive(string field)
    {
        // A rules file is written by a human. Rejecting "ClientID" would produce a rule that
        // silently never matches, which is the worst possible failure for a limiter: it looks
        // configured and enforces nothing.
        Assert.True(Sample().TryGetValue(field, out string? value));
        Assert.Equal("acct-1", value);
    }

    [Fact]
    public void Unset_optional_field_resolves_as_absent()
    {
        RequestContext context = new() { ClientId = "acct-1" };

        Assert.False(context.TryGetValue(RequestFields.Tier, out string? value));
        Assert.Null(value);
    }

    [Fact]
    public void Empty_string_is_treated_as_absent()
    {
        // This is a quota-evasion guard, not tidiness. If tier="" were a distinct value, a
        // caller could land in a different limiter bucket from tier-absent callers simply by
        // sending an empty header, and get a second allowance for free.
        RequestContext context = Sample() with { Tier = string.Empty };

        Assert.False(context.TryGetValue(RequestFields.Tier, out string? value));
        Assert.Null(value);
    }

    [Fact]
    public void Resolves_a_custom_attribute()
    {
        RequestContext context = Sample() with
        {
            Attributes = new Dictionary<string, string> { ["region"] = "eu-west-1" },
        };

        Assert.True(context.TryGetValue("region", out string? value));
        Assert.Equal("eu-west-1", value);
    }

    [Fact]
    public void Custom_attributes_resolve_case_insensitively_regardless_of_dictionary_comparer()
    {
        // The dictionary is caller-supplied and may use an ordinal comparer, so resolution
        // cannot rely on the dictionary's own lookup to be case-insensitive.
        RequestContext context = Sample() with
        {
            Attributes = new Dictionary<string, string>(StringComparer.Ordinal) { ["region"] = "eu-west-1" },
        };

        Assert.True(context.TryGetValue("REGION", out string? value));
        Assert.Equal("eu-west-1", value);
    }

    [Fact]
    public void Unknown_field_resolves_as_absent_rather_than_throwing()
    {
        // A rule naming a dimension this request does not carry must simply fail to match.
        // Throwing would turn one misconfigured rule into a failure for every request.
        Assert.False(Sample().TryGetValue("nonexistent", out string? value));
        Assert.Null(value);
    }

    [Fact]
    public void Missing_attributes_dictionary_is_not_an_error()
    {
        RequestContext context = Sample() with { Attributes = null };

        Assert.False(context.TryGetValue("region", out _));
    }

    [Fact]
    public void Null_field_name_throws()
    {
        Assert.Throws<ArgumentNullException>(() => Sample().TryGetValue(null!, out _));
    }
}
