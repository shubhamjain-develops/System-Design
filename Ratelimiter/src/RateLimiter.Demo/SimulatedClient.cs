using System.Text;
using RateLimiter.Core;

namespace RateLimiter.Demo;

/// <summary>
/// One simulated caller, with a traffic shape and a rolling record of how it was treated.
/// </summary>
internal sealed class SimulatedClient(
    string id,
    string tier,
    string endpoint,
    double requestsPerSecond,
    int burstEvery,
    int burstSize)
{
    private const int StripWidth = 40;

    private readonly Queue<char> _strip = new(StripWidth);
    private double _credit;
    private int _tick;

    public string Id => id;

    public string Tier => tier;

    public string Endpoint => endpoint;

    public long Allowed { get; private set; }

    public long Rejected { get; private set; }

    public string LastRule { get; private set; } = "-";

    public RateLimitAlgorithm LastAlgorithm { get; private set; }

    public long LastRemaining { get; private set; }

    /// <summary>
    /// How many requests this client makes on this tick.
    /// </summary>
    /// <remarks>
    /// Fractional rates accumulate as credit rather than rounding, so a client configured at
    /// half a request per second actually sends one every two seconds instead of one every tick
    /// or none at all.
    /// </remarks>
    public int RequestsThisTick(double tickSeconds)
    {
        _tick++;
        _credit += requestsPerSecond * tickSeconds;

        int count = (int)_credit;
        _credit -= count;

        if (burstEvery > 0 && _tick % burstEvery == 0)
        {
            count += burstSize;
        }

        return count;
    }

    public void Record(RateLimitDecision decision)
    {
        LastRule = decision.RuleName;
        LastAlgorithm = decision.Algorithm;
        LastRemaining = decision.RemainingPermits;

        if (decision.IsAllowed)
        {
            Allowed++;
            Push(decision.StoreFailureOccurred ? '!' : '#');
        }
        else
        {
            Rejected++;
            Push('.');
        }
    }

    /// <summary>
    /// A rolling strip of recent decisions, newest on the right.
    /// </summary>
    /// <remarks>
    /// The point of the demo. A token bucket absorbing a burst draws a solid run that thins as
    /// the bucket drains; a fixed window draws a hard edge at each boundary. The difference is
    /// obvious at a glance and hard to picture from a description.
    /// </remarks>
    public string Strip()
    {
        StringBuilder builder = new(StripWidth);
        builder.Append(' ', StripWidth - _strip.Count);

        foreach (char c in _strip)
        {
            builder.Append(c);
        }

        return builder.ToString();
    }

    private void Push(char c)
    {
        if (_strip.Count == StripWidth)
        {
            _strip.Dequeue();
        }

        _strip.Enqueue(c);
    }
}
