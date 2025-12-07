using Mostlylucid.Ephemeral;

namespace Mostlylucid.Ephemeral.Patterns.CircuitBreaker;

/// <summary>
/// Circuit breaker that uses the ephemeral signal window instead of maintaining its own state.
/// The circuit breaker has no state of its own - it just reads the ephemeral window.
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
    /// Check if the circuit is open (too many recent failures).
    /// </summary>
    public bool IsOpen<T>(EphemeralWorkCoordinator<T> coordinator)
    {
        var recentFailures = coordinator.GetSignalsSince(
            DateTimeOffset.UtcNow - _windowSize);

        return recentFailures.Count(s => s.Signal == _failureSignal) >= _threshold;
    }

    /// <summary>
    /// Check if the circuit is open using pattern matching.
    /// </summary>
    public bool IsOpenMatching<T>(EphemeralWorkCoordinator<T> coordinator, string pattern)
    {
        var recentSignals = coordinator.GetSignalsSince(
            DateTimeOffset.UtcNow - _windowSize);

        return recentSignals.Count(s => StringPatternMatcher.Matches(s.Signal, pattern)) >= _threshold;
    }

    /// <summary>
    /// Get the current failure count in the window.
    /// </summary>
    public int GetFailureCount<T>(EphemeralWorkCoordinator<T> coordinator)
    {
        var recentFailures = coordinator.GetSignalsSince(
            DateTimeOffset.UtcNow - _windowSize);

        return recentFailures.Count(s => s.Signal == _failureSignal);
    }

    /// <summary>
    /// Get the time until the circuit might close (based on oldest failure aging out).
    /// </summary>
    public TimeSpan? GetTimeUntilClose<T>(EphemeralWorkCoordinator<T> coordinator)
    {
        if (!IsOpen(coordinator))
            return null;

        var cutoff = DateTimeOffset.UtcNow - _windowSize;
        var recentFailures = coordinator.GetSignalsSince(cutoff)
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
/// Exception thrown when a circuit breaker is open.
/// </summary>
public class CircuitOpenException : Exception
{
    public TimeSpan? RetryAfter { get; }

    public CircuitOpenException(string message, TimeSpan? retryAfter = null)
        : base(message)
    {
        RetryAfter = retryAfter;
    }
}
