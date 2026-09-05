using System.Globalization;
using System.Text;
using RateLimiter.Core;
using RateLimiter.Core.Configuration;
using RateLimiter.Core.Resilience;
using RateLimiter.Core.Rules;
using RateLimiter.Core.Storage;
using RateLimiter.Core.Time;
using RateLimiter.Demo;

// A live view of the rule engine deciding real traffic. The value of watching it rather than
// reading about it is that the algorithms' differences are shapes: a token bucket absorbing a
// burst looks nothing like a fixed window resetting at a boundary, and the difference is
// immediate on screen and laborious in prose.

const double TickSeconds = 0.1;

// Keyboard control is only possible with a real console attached. When output or input is
// redirected — piping the demo to a file, or running it from a test — polling for a keypress
// throws rather than returning false, so interactivity is decided once, up front.
bool interactive = !Console.IsInputRedirected;

string rulesPath = "rules.json";
TimeSpan? runFor = null;

// Lets the store outage be demonstrated without a keypress, so the fail-open behaviour can be
// captured in a scripted run rather than only witnessed by hand.
TimeSpan? failAfter = null;

for (int i = 0; i < args.Length; i++)
{
    if (string.Equals(args[i], "--seconds", StringComparison.Ordinal) && i + 1 < args.Length
        && double.TryParse(args[i + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds))
    {
        runFor = TimeSpan.FromSeconds(seconds);
        i++;
        continue;
    }

    if (string.Equals(args[i], "--fail-after", StringComparison.Ordinal) && i + 1 < args.Length
        && double.TryParse(args[i + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out double failSeconds))
    {
        failAfter = TimeSpan.FromSeconds(failSeconds);
        i++;
        continue;
    }

    if (!args[i].StartsWith("--", StringComparison.Ordinal))
    {
        rulesPath = args[i];
    }
}

// A non-interactive run has no way to be stopped by a keypress, so it gets a finite default
// rather than running until it is killed.
runFor ??= interactive ? null : TimeSpan.FromSeconds(10);

if (!File.Exists(rulesPath))
{
    Console.Error.WriteLine($"Rules file not found: {Path.GetFullPath(rulesPath)}");
    return 1;
}

RuleSet loaded;
try
{
    using JsonFileRuleSource fileSource = new(rulesPath);
    loaded = fileSource.Current;

    if (!fileSource.Diagnostics.IsDefaultOrEmpty)
    {
        Console.WriteLine("Rule diagnostics:");
        foreach (RuleDiagnostic diagnostic in fileSource.Diagnostics)
        {
            Console.WriteLine($"  {diagnostic}");
        }

        if (interactive)
        {
            Console.WriteLine();
            Console.WriteLine("Press any key to start...");
            Console.ReadKey(intercept: true);
        }
    }
}
catch (RuleConfigurationException ex)
{
    // Startup deliberately refuses to run with unusable rules rather than starting unlimited.
    Console.Error.WriteLine(ex.Message);
    return 1;
}

OverridableRuleSource rules = new(loaded);
ToggleableStore store = new(new InMemoryRateLimitStore(SystemClock.Instance));

RateLimitEngine engine = new(
    rules,
    store,
    options: new RateLimiterOptions
    {
        OnStoreFailure = StoreFailurePolicy.FailOpen,
        CircuitBreaker = new CircuitBreaker(failureThreshold: 5, openDuration: TimeSpan.FromSeconds(3)),
    });

SimulatedClient[] clients =
[
    // Steady, well within its allowance. Should be almost entirely admitted.
    new("acct-quiet", "free", "/v1/search", requestsPerSecond: 0.8, burstEvery: 0, burstSize: 0),

    // Bursty but not abusive: the case a token bucket exists to tolerate and a fixed window
    // handles badly.
    new("acct-bursty", "free", "/v1/search", requestsPerSecond: 0.5, burstEvery: 25, burstSize: 7),

    // Sustained overload. Should be rejected most of the time under every algorithm.
    new("acct-greedy", "free", "/v1/search", requestsPerSecond: 6.0, burstEvery: 0, burstSize: 0),

    // Same traffic as the greedy client, four times the allowance. Tiering as policy.
    new("acct-pro", "pro", "/v1/search", requestsPerSecond: 6.0, burstEvery: 0, burstSize: 0),

    // Precision endpoint: matched by a higher-priority rule regardless of tier.
    new("acct-payer", "pro", "/v1/payments", requestsPerSecond: 1.2, burstEvery: 40, burstSize: 4),

    // Matches no specific rule, so it lands on the catch-all.
    new("acct-other", "free", "/v1/admin", requestsPerSecond: 2.5, burstEvery: 0, burstSize: 0),
];

using CancellationTokenSource cancellation = new();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cancellation.Cancel();
};

bool canRedraw = TryPrepareConsole();
DateTimeOffset started = DateTimeOffset.UtcNow;
DateTimeOffset lastRender = DateTimeOffset.MinValue;

while (!cancellation.IsCancellationRequested)
{
    TimeSpan runningFor = DateTimeOffset.UtcNow - started;

    if (runFor is { } limit && runningFor >= limit)
    {
        break;
    }

    if (failAfter is { } outageAt && runningFor >= outageAt)
    {
        store.IsFailing = true;
    }

    if (interactive && Console.KeyAvailable)
    {
        ConsoleKey key = Console.ReadKey(intercept: true).Key;

        if (key is ConsoleKey.Q or ConsoleKey.Escape)
        {
            break;
        }

        if (key == ConsoleKey.F)
        {
            store.IsFailing = !store.IsFailing;
        }

        if (key == ConsoleKey.A)
        {
            rules.CycleAlgorithm();
        }
    }

    foreach (SimulatedClient client in clients)
    {
        int count = client.RequestsThisTick(TickSeconds);

        for (int i = 0; i < count; i++)
        {
            RequestContext request = new()
            {
                ClientId = client.Id,
                Tier = client.Tier,
                Endpoint = client.Endpoint,
                Method = "GET",
            };

            RateLimitDecision decision = await engine.EvaluateAsync(request, 1, cancellation.Token)
                .ConfigureAwait(false);

            client.Record(decision);
        }
    }

    // Redrawing in place is cheap and looks live. Appending frames to a redirected stream is
    // neither, so a non-interactive run reports periodically instead of every tick.
    if (canRedraw || DateTimeOffset.UtcNow - lastRender >= TimeSpan.FromSeconds(2))
    {
        Render(clients, engine, rules, store, started, canRedraw);
        lastRender = DateTimeOffset.UtcNow;
    }

    try
    {
        await Task.Delay(TimeSpan.FromSeconds(TickSeconds), cancellation.Token).ConfigureAwait(false);
    }
    catch (OperationCanceledException)
    {
        break;
    }
}

Console.WriteLine();
Console.WriteLine("Final counters: " + engine.Metrics);
return 0;

static bool TryPrepareConsole()
{
    try
    {
        Console.CursorVisible = false;
        Console.Clear();
        return !Console.IsOutputRedirected;
    }
    catch (IOException)
    {
        // Redirected or non-interactive output. Fall back to appending lines rather than
        // failing: piping the demo to a file is a reasonable thing to want.
        return false;
    }
}

static void Render(
    IReadOnlyList<SimulatedClient> clients,
    RateLimitEngine engine,
    OverridableRuleSource rules,
    ToggleableStore store,
    DateTimeOffset started,
    bool canRedraw)
{
    StringBuilder screen = new();

    string algorithmLabel = rules.Override is { } forced
        ? $"forced to {forced}"
        : "as configured per rule";

    string storeLabel = store.IsFailing ? "FAILING (fail-open)" : "healthy";
    TimeSpan elapsed = DateTimeOffset.UtcNow - started;

    screen.Append(CultureInfo.InvariantCulture, $"Rate limiter demo   elapsed {elapsed:mm\\:ss}   algorithm: {algorithmLabel}   store: {storeLabel}");
    screen.AppendLine();
    screen.Append(CultureInfo.InvariantCulture, $"{engine.Metrics}");
    screen.AppendLine();
    screen.AppendLine();
    screen.AppendLine("client         tier  endpoint       rule             algorithm             rem   allow  rejct  recent (# allowed  . rejected  ! fail-open)");
    screen.AppendLine(new string('-', 148));

    foreach (SimulatedClient client in clients)
    {
        screen.Append(CultureInfo.InvariantCulture, $"{client.Id,-14} {client.Tier,-5} {client.Endpoint,-14} ");
        screen.Append(CultureInfo.InvariantCulture, $"{Truncate(client.LastRule, 16),-16} {client.LastAlgorithm,-21} ");
        screen.Append(CultureInfo.InvariantCulture, $"{FormatRemaining(client.LastRemaining),4}  {client.Allowed,6} {client.Rejected,6}  ");
        screen.Append(client.Strip());
        screen.AppendLine();
    }

    screen.AppendLine();
    screen.AppendLine("[a] cycle algorithm for every rule   [f] toggle store failure   [q] quit");

    if (canRedraw)
    {
        try
        {
            Console.SetCursorPosition(0, 0);
            Console.Write(screen.ToString());
            return;
        }
        catch (IOException)
        {
            // Console lost its cursor support mid-run; fall through to plain output.
        }
        catch (ArgumentOutOfRangeException)
        {
            // Window resized smaller than the cursor position.
        }
    }

    Console.Write(screen.ToString());
}

static string FormatRemaining(long remaining) =>
    remaining == long.MaxValue ? "-" : remaining.ToString(CultureInfo.InvariantCulture);

static string Truncate(string value, int width) =>
    value.Length <= width ? value : value[..(width - 1)] + "~";
