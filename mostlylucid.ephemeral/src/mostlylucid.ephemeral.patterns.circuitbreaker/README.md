# Mostlylucid.Ephemeral.Patterns.CircuitBreaker

Stateless circuit breaker that reads state from the ephemeral signal window.

## Installation

```bash
dotnet add package mostlylucid.ephemeral.patterns.circuitbreaker
```

## Usage

```csharp
var breaker = new SignalBasedCircuitBreaker(
    failureSignal: "api.failure",
    threshold: 5,
    windowSize: TimeSpan.FromSeconds(30));

if (breaker.IsOpen(coordinator))
{
    var retryAfter = breaker.GetTimeUntilClose(coordinator);
    throw new CircuitOpenException("Too many failures", retryAfter);
}
```

## Full Source (~80 lines)

```csharp
using Mostlylucid.Ephemeral;

namespace Mostlylucid.Ephemeral.Patterns.CircuitBreaker;

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

    public bool IsOpen<T>(EphemeralWorkCoordinator<T> coordinator)
    {
        var recentFailures = coordinator.GetSignalsSince(
            DateTimeOffset.UtcNow - _windowSize);
        return recentFailures.Count(s => s.Signal == _failureSignal) >= _threshold;
    }

    public bool IsOpenMatching<T>(EphemeralWorkCoordinator<T> coordinator, string pattern)
    {
        var recentSignals = coordinator.GetSignalsSince(
            DateTimeOffset.UtcNow - _windowSize);
        return recentSignals.Count(s => StringPatternMatcher.Matches(s.Signal, pattern)) >= _threshold;
    }

    public int GetFailureCount<T>(EphemeralWorkCoordinator<T> coordinator)
    {
        var recentFailures = coordinator.GetSignalsSince(
            DateTimeOffset.UtcNow - _windowSize);
        return recentFailures.Count(s => s.Signal == _failureSignal);
    }

    public TimeSpan? GetTimeUntilClose<T>(EphemeralWorkCoordinator<T> coordinator)
    {
        if (!IsOpen(coordinator)) return null;

        var cutoff = DateTimeOffset.UtcNow - _windowSize;
        var recentFailures = coordinator.GetSignalsSince(cutoff)
            .Where(s => s.Signal == _failureSignal)
            .OrderBy(s => s.Timestamp)
            .ToList();

        if (recentFailures.Count < _threshold) return null;

        var oldestRelevant = recentFailures[recentFailures.Count - _threshold];
        return oldestRelevant.Timestamp + _windowSize - DateTimeOffset.UtcNow;
    }
}

public class CircuitOpenException : Exception
{
    public TimeSpan? RetryAfter { get; }
    public CircuitOpenException(string message, TimeSpan? retryAfter = null) : base(message)
        => RetryAfter = retryAfter;
}
```

## License

MIT
