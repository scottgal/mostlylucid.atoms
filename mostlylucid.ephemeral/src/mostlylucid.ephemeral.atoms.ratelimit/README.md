# Mostlylucid.Ephemeral.Atoms.RateLimit

Bounded rate limiting for coordinators with signal-driven control.

## Key ideas

- `RateLimitAtom` wraps a token-bucket limiter and emits signals whenever the configuration changes.
- Listens for `rate.limit.*` commands (e.g., `rate.limit.increase:1`, `rate.limit.decrease:2`, `rate.limit.set:100`, `rate.limit.burst:50`) so operators can control throughput at runtime.
- Call `AcquireAsync` before starting a unit of work to honor the current rate and keep the rest of the system self-regulating.

## Example

```csharp
var sink = new SignalSink();
var limiter = new RateLimitAtom(sink, new RateLimitOptions { InitialRatePerSecond = 5, Burst = 10 });

await using var coordinator = new EphemeralWorkCoordinator<JobItem>(
    async (item, ct) =>
    {
        await limiter.AcquireAsync(ct);
        await ProcessItemAsync(item, ct);
    },
    new EphemeralOptions { Signals = sink });

// Control signals (send from admin console or another job)
sink.Raise("rate.limit.increase:2");
sink.Raise("rate.limit.burst:20");
```

Keep the sink alongside your coordinator so rate controls, logging, and responsibility signals share the same window.
