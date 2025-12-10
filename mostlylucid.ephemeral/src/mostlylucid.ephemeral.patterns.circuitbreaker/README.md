# Mostlylucid.Ephemeral.Patterns.CircuitBreaker

[![NuGet](https://img.shields.io/nuget/v/mostlylucid.ephemeral.patterns.circuitbreaker.svg)](https://www.nuget.org/packages/mostlylucid.ephemeral.patterns.circuitbreaker)

> 🚨🚨 WARNING 🚨🚨 - Though in the 1.x range of version THINGS WILL STILL BREAK. This is the lab for developing this concept when stabilized it'll becoe the first *stylo*flow release 🚨🚨🚨


Stateless circuit breaker that reads state from the ephemeral signal window.

```bash
dotnet add package mostlylucid.ephemeral.patterns.circuitbreaker
```

## Quick Start

```csharp
using Mostlylucid.Ephemeral.Patterns.CircuitBreaker;

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

---

## All Options

```csharp
new SignalBasedCircuitBreaker(
    // Signal name to count as failure
    // Default: "failure"
    failureSignal: "api.failure",

    // Number of failures to open circuit
    // Default: 5
    threshold: 5,

    // Time window for counting failures
    // Default: 30 seconds
    windowSize: TimeSpan.FromSeconds(30)
)
```

---

## API Reference

```csharp
// Check if circuit is open (too many failures)
bool isOpen = breaker.IsOpen(coordinator);

// Check using glob pattern matching
bool isOpen = breaker.IsOpenMatching(coordinator, "error.*");

// Get current failure count
int failures = breaker.GetFailureCount(coordinator);

// Get time until circuit closes (null if already closed)
TimeSpan? retryAfter = breaker.GetTimeUntilClose(coordinator);
```

---

## How It Works

The circuit breaker is **stateless** - it calculates state from the coordinator's signal window on each check:

```
Window: [now - 30s] ────────────────────> [now]
        failure  failure  failure  failure  failure
        ─────────────────────────────────────────────
        Count: 5 failures
        Threshold: 5
        Result: CIRCUIT OPEN
```

When the oldest failure ages out of the window, the circuit automatically closes.

---

## Example: API Protection

```csharp
var breaker = new SignalBasedCircuitBreaker("api.failure", threshold: 5);

await using var coordinator = new EphemeralWorkCoordinator<ApiRequest>(
    async (req, ct) =>
    {
        // Check circuit before calling
        if (breaker.IsOpen(coordinator))
        {
            var retry = breaker.GetTimeUntilClose(coordinator);
            throw new CircuitOpenException("API circuit open", retry);
        }

        try
        {
            await CallApi(req, ct);
        }
        catch
        {
            coordinator.Signal("api.failure");
            throw;
        }
    });
```

---

## Example: Pattern-Based Circuit

```csharp
var breaker = new SignalBasedCircuitBreaker(
    failureSignal: "error",
    threshold: 10,
    windowSize: TimeSpan.FromMinutes(1));

// Opens on any signal matching "error.*"
if (breaker.IsOpenMatching(coordinator, "error.*"))
{
    // Circuit is open due to various error types
}
```

---

## CircuitOpenException

```csharp
public class CircuitOpenException : Exception
{
    // Time until circuit might close
    public TimeSpan? RetryAfter { get; }
}

// Usage
try
{
    await service.CallAsync();
}
catch (CircuitOpenException ex)
{
    if (ex.RetryAfter.HasValue)
        await Task.Delay(ex.RetryAfter.Value);
}
```

---

## Related Packages

| Package                                                                                                                         | Description       |
|---------------------------------------------------------------------------------------------------------------------------------|-------------------|
| [mostlylucid.ephemeral](https://www.nuget.org/packages/mostlylucid.ephemeral)                                                   | Core library      |
| [mostlylucid.ephemeral.patterns.anomalydetector](https://www.nuget.org/packages/mostlylucid.ephemeral.patterns.anomalydetector) | Anomaly detection |
| [mostlylucid.ephemeral.atoms.signalaware](https://www.nuget.org/packages/mostlylucid.ephemeral.atoms.signalaware)               | Signal-aware atom |
| [mostlylucid.ephemeral.complete](https://www.nuget.org/packages/mostlylucid.ephemeral.complete)                                 | All in one DLL    |

## License

Unlicense (public domain)