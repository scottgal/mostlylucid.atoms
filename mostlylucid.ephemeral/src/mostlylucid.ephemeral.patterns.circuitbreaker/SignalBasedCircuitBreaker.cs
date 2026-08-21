namespace Mostlylucid.Ephemeral.Patterns.CircuitBreaker;

/// <summary>
///     Circuit breaker that uses the ephemeral signal window instead of maintaining its own state.
///     The circuit breaker has no state of its own - it just reads the ephemeral window.
///     Two data sources are supported: an EphemeralWorkCoordinator's per-operation signal window
///     (for signals raised via <c>op.Signal(...)</c> inside a body that receives the operation),
///     and a raw SignalSink (for atoms like RetryAtom whose body signature has no access to the
///     operation object and can only raise directly on the sink). These are genuinely different
///     stores — a signal raised straight on a sink does not appear in any operation's own signal
///     list — so both overloads are needed rather than one silently missing the other's failures.
/// </summary>
public class SignalBasedCircuitBreaker
{
    private readonly string _failureSignal;
    private readonly int _threshold;
    private readonly TimeSpan _windowSize;

    public SignalBasedCircuitBreaker(
        string failureSignal = "failure",
        int threshold = 5,
        TimeSpan? windowSize = null)
    {
        _failureSignal = failureSignal;
        _threshold = threshold;
        _windowSize = windowSize ?? TimeSpan.FromSeconds(30);
    }

    /// <summary>
    ///     Check if the circuit is open (too many recent failures) based on a coordinator's
    ///     per-operation signal window.
    /// </summary>
    public bool IsOpen<T>(EphemeralWorkCoordinator<T> coordinator)
    {
        return CountMatching(coordinator.GetSignalsSince(CutoffNow()), _failureSignal) >= _threshold;
    }

    /// <summary>
    ///     Check if the circuit is open (too many recent failures) based on a raw SignalSink —
    ///     for atoms that raise failure signals directly on the sink rather than through an
    ///     EphemeralOperation.
    /// </summary>
    public bool IsOpen(SignalSink sink)
    {
        return CountMatching(sink.Sense(e => e.Timestamp >= CutoffNow()), _failureSignal) >= _threshold;
    }

    /// <summary>
    ///     Check if the circuit is open using pattern matching, against a coordinator's
    ///     per-operation signal window.
    /// </summary>
    public bool IsOpenMatching<T>(EphemeralWorkCoordinator<T> coordinator, string pattern)
    {
        return CountMatchingPattern(coordinator.GetSignalsSince(CutoffNow()), pattern) >= _threshold;
    }

    /// <summary>
    ///     Check if the circuit is open using pattern matching, against a raw SignalSink.
    /// </summary>
    public bool IsOpenMatching(SignalSink sink, string pattern)
    {
        return CountMatchingPattern(sink.Sense(e => e.Timestamp >= CutoffNow()), pattern) >= _threshold;
    }

    /// <summary>
    ///     Get the current failure count in the window, from a coordinator's per-operation signal
    ///     window.
    /// </summary>
    public int GetFailureCount<T>(EphemeralWorkCoordinator<T> coordinator)
    {
        return CountMatching(coordinator.GetSignalsSince(CutoffNow()), _failureSignal);
    }

    /// <summary>
    ///     Get the current failure count in the window, from a raw SignalSink.
    /// </summary>
    public int GetFailureCount(SignalSink sink)
    {
        return CountMatching(sink.Sense(e => e.Timestamp >= CutoffNow()), _failureSignal);
    }

    /// <summary>
    ///     Get the time until the circuit might close (based on oldest failure aging out), from a
    ///     coordinator's per-operation signal window.
    /// </summary>
    public TimeSpan? GetTimeUntilClose<T>(EphemeralWorkCoordinator<T> coordinator)
    {
        return TimeUntilClose(coordinator.GetSignalsSince(CutoffNow()));
    }

    /// <summary>
    ///     Get the time until the circuit might close (based on oldest failure aging out), from a
    ///     raw SignalSink.
    /// </summary>
    public TimeSpan? GetTimeUntilClose(SignalSink sink)
    {
        return TimeUntilClose(sink.Sense(e => e.Timestamp >= CutoffNow()));
    }

    private DateTimeOffset CutoffNow()
    {
        return DateTimeOffset.UtcNow - _windowSize;
    }

    private static int CountMatching(IReadOnlyList<SignalEvent> signals, string signal)
    {
        return signals.Count(s => s.Signal == signal);
    }

    private static int CountMatchingPattern(IReadOnlyList<SignalEvent> signals, string pattern)
    {
        return signals.Count(s => StringPatternMatcher.Matches(s.Signal, pattern));
    }

    private TimeSpan? TimeUntilClose(IReadOnlyList<SignalEvent> signalsInWindow)
    {
        var recentFailures = signalsInWindow
            .Where(s => s.Signal == _failureSignal)
            .OrderBy(s => s.Timestamp)
            .ToList();

        if (recentFailures.Count < _threshold)
            return null;

        var oldestRelevant = recentFailures[recentFailures.Count - _threshold];
        var ageOutTime = oldestRelevant.Timestamp + _windowSize;

        return ageOutTime - DateTimeOffset.UtcNow;
    }
}

/// <summary>
///     Exception thrown when a circuit breaker is open.
/// </summary>
public class CircuitOpenException : Exception
{
    public CircuitOpenException(string message, TimeSpan? retryAfter = null)
        : base(message)
    {
        RetryAfter = retryAfter;
    }

    public TimeSpan? RetryAfter { get; }
}
