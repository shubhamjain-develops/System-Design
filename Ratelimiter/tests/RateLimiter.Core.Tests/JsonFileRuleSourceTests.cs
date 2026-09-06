using RateLimiter.Core.Configuration;
using RateLimiter.Core.Rules;

namespace RateLimiter.Core.Tests;

/// <summary>
/// Covers reloading, and the guarantee that a broken file cannot disable rate limiting.
/// </summary>
public sealed class JsonFileRuleSourceTests : IDisposable
{
    private readonly string _directory =
        Directory.CreateTempSubdirectory("ratelimiter-rules-tests").FullName;

    private string WriteRules(string json, string name = "rules.json")
    {
        string path = Path.Combine(_directory, name);
        File.WriteAllText(path, json);
        return path;
    }

    private static string Document(int limit) => $$"""
        { "rules": [ { "name": "r", "policy": { "algorithm": "fixedWindow", "limit": {{limit}}, "windowSeconds": 60 } } ] }
        """;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp directory is not worth failing a test over.
        }
    }

    [Fact]
    public void Loads_rules_from_disk()
    {
        using JsonFileRuleSource source = new(WriteRules(Document(5)));

        Assert.Equal(5, source.Current.Rules[0].Policy.Limit);
    }

    [Fact]
    public void A_missing_file_fails_at_startup_rather_than_starting_unlimited()
    {
        // Startup is the one case with no previous good state to fall back on. Starting with no
        // limits at all is worse than not starting.
        Assert.Throws<RuleConfigurationException>(
            () => new JsonFileRuleSource(Path.Combine(_directory, "does-not-exist.json")));
    }

    [Fact]
    public void An_invalid_file_fails_at_startup_and_says_why()
    {
        RuleConfigurationException ex = Assert.Throws<RuleConfigurationException>(
            () => new JsonFileRuleSource(WriteRules("{ nonsense")));

        Assert.NotEmpty(ex.Diagnostics);
    }

    [Fact]
    public void Reload_picks_up_a_changed_file()
    {
        string path = WriteRules(Document(5));
        using JsonFileRuleSource source = new(path);

        File.WriteAllText(path, Document(50));
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(1));

        Assert.True(source.Reload());
        Assert.Equal(50, source.Current.Rules[0].Policy.Limit);
    }

    [Fact]
    public void Reload_is_a_no_op_when_the_file_is_unchanged()
    {
        using JsonFileRuleSource source = new(WriteRules(Document(5)));

        Assert.False(source.Reload());
        Assert.Equal(5, source.Current.Rules[0].Policy.Limit);
    }

    [Fact]
    public void A_broken_reload_keeps_the_previous_rules_in_force()
    {
        // The most important behaviour in this file. Falling back to an empty rule set would
        // turn a configuration typo into a gateway with no limits at all, where every response
        // still says 200 and nothing looks wrong.
        string path = WriteRules(Document(5));
        using JsonFileRuleSource source = new(path);

        File.WriteAllText(path, "{ this is broken");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(1));

        Assert.False(source.Reload());
        Assert.Single(source.Current.Rules);
        Assert.Equal(5, source.Current.Rules[0].Policy.Limit);
    }

    [Fact]
    public void Failed_reloads_are_counted_so_stale_rules_are_detectable()
    {
        // A node serving stale limits looks exactly like one serving current limits. This
        // counter is the only thing that distinguishes them, which makes it the thing to alert
        // on.
        string path = WriteRules(Document(5));
        using JsonFileRuleSource source = new(path);

        Assert.Equal(0, source.FailedReloadCount);

        File.WriteAllText(path, "{ broken");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(1));
        source.Reload();

        Assert.Equal(1, source.FailedReloadCount);
    }

    [Fact]
    public void A_recovered_file_is_picked_up_after_a_failure()
    {
        string path = WriteRules(Document(5));
        using JsonFileRuleSource source = new(path);

        File.WriteAllText(path, "{ broken");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(1));
        source.Reload();

        File.WriteAllText(path, Document(99));
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(2));

        Assert.True(source.Reload());
        Assert.Equal(99, source.Current.Rules[0].Policy.Limit);
    }

    [Fact]
    public void Current_returns_a_snapshot_that_a_reload_cannot_mutate()
    {
        // A request evaluated during a reload must see one coherent version of the rules.
        string path = WriteRules(Document(5));
        using JsonFileRuleSource source = new(path);

        RuleSet before = source.Current;

        File.WriteAllText(path, Document(50));
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(1));
        source.Reload();

        Assert.Equal(5, before.Rules[0].Policy.Limit);
        Assert.Equal(50, source.Current.Rules[0].Policy.Limit);
    }

    [Fact]
    public void Diagnostics_from_the_loaded_file_are_available()
    {
        string json = """
            {
              "rules": [
                { "name": "catch-all", "priority": 1,
                  "policy": { "algorithm": "fixedWindow", "limit": 1, "windowSeconds": 1 } },
                { "name": "shadowed", "priority": 2, "match": { "tier": "free" },
                  "policy": { "algorithm": "fixedWindow", "limit": 1, "windowSeconds": 1 } }
              ]
            }
            """;

        using JsonFileRuleSource source = new(WriteRules(json));

        Assert.Contains(source.Diagnostics, d => d.RuleName == "shadowed");
    }
}
