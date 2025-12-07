# Mostlylucid.Ephemeral.Patterns.CircuitBreaker

Stateless circuit breaker that reads state from the ephemeral signal window.

## Key Insight

The circuit breaker has **no state of its own** - it just reads the coordinator's signal window. This means:
- No shared state to synchronize
- Circuit naturally "closes" as old failures age out
- Multiple consumers can check independently

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

## License

MIT
