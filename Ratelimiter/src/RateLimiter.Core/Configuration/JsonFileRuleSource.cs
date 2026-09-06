using System.Collections.Immutable;
using RateLimiter.Core.Rules;

namespace RateLimiter.Core.Configuration;

/// <summary>
/// Loads rules from a JSON file and optionally re-reads it on an interval.
/// </summary>
/// <remarks>
/// <para>
/// This is the local stand-in for the architecture the design assumes: a rule service publishes
/// limits, and each limiter node caches them so that a request never waits on the rule store.
/// A file replaces the service; the caching, the snapshot semantics and the failure handling are
/// the same either way, which is what makes this worth building rather than hard-coding rules.
/// </para>
/// <para>
/// <strong>Polling, not just notification.</strong> The published design uses pub/sub with
/// polling as a reconciliation fallback, and that pairing is deliberate: pub/sub delivers
/// promptly but a missed message leaves a node serving stale limits forever, with nothing to
/// correct it. A poll is the backstop that bounds how long any node can be wrong. Only the poll
/// is implemented here, because it is the half that guarantees convergence — a push can be added
/// in front of it without changing anything else.
/// </para>
/// <para>
/// <strong>A broken file must never disable rate limiting.</strong> If a reload fails to parse,
/// the previous rule set stays in force and the failure is counted. The alternative — falling
/// back to an empty rule set — turns a typo in a configuration file into an outage-shaped
/// event where every limit silently disappears and every response still says 200. Startup is
/// the deliberate exception: a file that cannot be read at construction throws, because there
/// is no previous good state to fall back to and starting unlimited is worse than not starting.
/// </para>
/// </remarks>
public sealed class JsonFileRuleSource : IRuleSource, IDisposable
{
    private readonly string _path;
    private readonly Lock _gate = new();
    private readonly Timer? _timer;

    private RuleSet _current;
    private ImmutableArray<RuleDiagnostic> _diagnostics;
    private DateTime _lastWriteUtc;
    private int _failedReloads;

    /// <summary>
    /// Loads rules from a file, optionally polling it for changes.
    /// </summary>
    /// <param name="path">The JSON file.</param>
    /// <param name="pollInterval">
    /// How often to re-read. Omit to disable polling and reload only when
    /// <see cref="Reload"/> is called.
    /// </param>
    /// <exception cref="RuleConfigurationException">
    /// The file is missing, unreadable, or does not contain a usable rule set.
    /// </exception>
    public JsonFileRuleSource(string path, TimeSpan? pollInterval = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        _path = Path.GetFullPath(path);

        if (!TryLoad(out RuleSet? loaded, out ImmutableArray<RuleDiagnostic> diagnostics, out string? failure))
        {
            throw new RuleConfigurationException(
                $"Could not load rate-limit rules from '{_path}': {failure}",
                diagnostics);
        }

        _current = loaded;
        _diagnostics = diagnostics;

        if (pollInterval is { } interval)
        {
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(interval, TimeSpan.Zero);
            _timer = new Timer(_ => Reload(), null, interval, interval);
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// Returns an immutable snapshot, so a request evaluated during a reload sees one coherent
    /// version of the rules rather than a mixture of the old and the new.
    /// </remarks>
    public RuleSet Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    /// <summary>
    /// Diagnostics from the most recent successful load.
    /// </summary>
    public ImmutableArray<RuleDiagnostic> Diagnostics
    {
        get
        {
            lock (_gate)
            {
                return _diagnostics;
            }
        }
    }

    /// <summary>
    /// How many reloads have failed since construction.
    /// </summary>
    /// <remarks>
    /// Exposed because a node quietly serving stale limits looks identical to one serving
    /// current limits. This is the number that distinguishes them, and it is the one worth
    /// alerting on.
    /// </remarks>
    public int FailedReloadCount
    {
        get
        {
            lock (_gate)
            {
                return _failedReloads;
            }
        }
    }

    /// <summary>
    /// Re-reads the file if it has changed.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> when a new rule set was applied; <see langword="false"/> when the
    /// file was unchanged or could not be loaded.
    /// </returns>
    /// <remarks>
    /// Public so that a test can reload deterministically instead of waiting for a timer, and so
    /// that a host can drive reloads from its own signal rather than running a second one.
    /// </remarks>
    public bool Reload()
    {
        DateTime writeTime;
        try
        {
            writeTime = File.GetLastWriteTimeUtc(_path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            lock (_gate)
            {
                _failedReloads++;
            }

            return false;
        }

        lock (_gate)
        {
            if (writeTime == _lastWriteUtc)
            {
                return false;
            }
        }

        if (!TryLoad(out RuleSet? loaded, out ImmutableArray<RuleDiagnostic> diagnostics, out _))
        {
            // Keep serving the previous rules. A configuration typo must not become an
            // unlimited gateway.
            lock (_gate)
            {
                _failedReloads++;
            }

            return false;
        }

        lock (_gate)
        {
            _current = loaded;
            _diagnostics = diagnostics;
        }

        return true;
    }

    /// <inheritdoc />
    public void Dispose() => _timer?.Dispose();

    private bool TryLoad(
        out RuleSet loaded,
        out ImmutableArray<RuleDiagnostic> diagnostics,
        out string? failure)
    {
        loaded = RuleSet.Empty;
        diagnostics = [];

        string json;
        DateTime writeTime;

        try
        {
            // Read the write time before the content. If the file is rewritten between the two,
            // the recorded time is older than what was read, so the next poll re-reads rather
            // than concluding nothing changed — the safe direction to be wrong in.
            writeTime = File.GetLastWriteTimeUtc(_path);
            json = File.ReadAllText(_path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            failure = ex.Message;
            return false;
        }

        RuleParseResult result = JsonRuleParser.Parse(json);
        diagnostics = result.Diagnostics;

        if (!result.Succeeded)
        {
            failure = string.Join(
                "; ",
                result.Diagnostics
                    .Where(d => d.Severity == RuleDiagnosticSeverity.Error)
                    .Select(d => d.Message));

            return false;
        }

        loaded = result.RuleSet!;
        failure = null;

        lock (_gate)
        {
            _lastWriteUtc = writeTime;
        }

        return true;
    }
}

/// <summary>
/// Thrown when rules cannot be loaded at startup.
/// </summary>
public sealed class RuleConfigurationException : Exception
{
    /// <summary>Creates the exception with diagnostics attached.</summary>
    /// <param name="message">What went wrong.</param>
    /// <param name="diagnostics">The individual problems found.</param>
    public RuleConfigurationException(string message, ImmutableArray<RuleDiagnostic> diagnostics)
        : base(message)
    {
        Diagnostics = diagnostics;
    }

    /// <summary>Creates the exception.</summary>
    /// <param name="message">What went wrong.</param>
    public RuleConfigurationException(string message)
        : this(message, [])
    {
    }

    /// <summary>Creates the exception.</summary>
    public RuleConfigurationException()
        : this("Rate-limit rules could not be loaded.", [])
    {
    }

    /// <summary>Creates the exception.</summary>
    /// <param name="message">What went wrong.</param>
    /// <param name="innerException">The underlying cause.</param>
    public RuleConfigurationException(string message, Exception innerException)
        : base(message, innerException)
    {
        Diagnostics = [];
    }

    /// <summary>
    /// The individual problems found while loading.
    /// </summary>
    public ImmutableArray<RuleDiagnostic> Diagnostics { get; }
}
