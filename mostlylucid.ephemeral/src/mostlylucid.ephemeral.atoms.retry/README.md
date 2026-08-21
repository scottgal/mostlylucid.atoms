# Mostlylucid.Ephemeral.Atoms.Retry

[![NuGet](https://img.shields.io/nuget/v/mostlylucid.ephemeral.atoms.retry.svg)](https://www.nuget.org/packages/mostlylucid.ephemeral.atoms.retry)

Signal-driven retry with backoff for transient failures. Every attempt failure, exhaustion, and
eventual success is raised onto the shared signal sink — so a `SignalBasedCircuitBreaker` (or
anything else watching the sink) can observe real failure history, instead of retries being
invisible inside this atom.

```bash
dotnet add package mostlylucid.ephemeral.atoms.retry
```

## Quick Start

```csharp
using Mostlylucid.Ephemeral.Atoms.Retry;

await using var atom = new RetryAtom<ApiRequest>(
    async (req, ct) => await CallExternalApi(req, ct),
    backoff: BackoffStrategies.Exponential(TimeSpan.FromMilliseconds(100)),
    maxTotalDuration: TimeSpan.FromSeconds(30),
    maxAttempts: 3);

// Automatically retries on failure
await atom.EnqueueAsync(new ApiRequest("https://api.example.com"));

await atom.DrainAsync();
```

---

## All Options

```csharp
new RetryAtom<T>(
    // Required: async work body
    body: async (item, ct) => await ProcessAsync(item, ct),

    // Required, no default: delay-per-attempt. Use a named strategy below, or your own
    // Func<int, TimeSpan>. There is deliberately no baked-in default here — a retry
    // schedule is a caller decision, not a library constant.
    backoff: BackoffStrategies.Exponential(TimeSpan.FromMilliseconds(100)),

    // Required, no default: bounds the ENTIRE retry loop for one item — all attempts and
    // backoff waits combined. The underlying coordinator owns the concurrency slot and must
    // not let one item hold it forever.
    maxTotalDuration: TimeSpan.FromSeconds(30),

    // Max attempts including the first
    // Default: 3
    maxAttempts: 3,

    // Max concurrent operations
    // Default: Environment.ProcessorCount
    maxConcurrency: 4,

    // Shared signal sink — required for signals to be observable at all, and required if
    // you also supply a breaker
    // Default: null
    signals: sharedSink,

    // Optional circuit breaker gating intake (see below)
    // Default: null
    breaker: null,

    // Optional clock for backoff waits, for testability
    // Default: TimeProvider.System
    timeProvider: null
)
```

---

## API Reference

```csharp
// Enqueue with automatic retry. Throws CircuitOpenException if a breaker is supplied and open.
ValueTask<long> id = await atom.EnqueueAsync(item, ct);

// Drain
await atom.DrainAsync(ct);

await atom.DisposeAsync();
```

## Signals

When `signals` is supplied, this atom raises:

| Signal                                   | When                                                             |
|-------------------------------------------|-------------------------------------------------------------------|
| `RetryAtom<T>.AttemptFailedSignal`         | An attempt fails and another will be tried                       |
| `RetryAtom<T>.ExhaustedSignal`              | The final attempt fails and no more remain                       |
| `RetryAtom<T>.SucceededAfterRetrySignal`    | An item succeeds after at least one prior failure                |

---

## Backoff Strategies

Use `BackoffStrategies` for named, reusable strategies instead of writing a lambda every time.

```csharp
using Mostlylucid.Ephemeral.Atoms.Retry;

BackoffStrategies.Constant(TimeSpan.FromMilliseconds(500));                 // Always 500ms
BackoffStrategies.Linear(TimeSpan.FromMilliseconds(50));                    // 50ms, 100ms, 150ms, ...
BackoffStrategies.Exponential(TimeSpan.FromMilliseconds(100));              // 100ms, 200ms, 400ms, ...
BackoffStrategies.Exponential(TimeSpan.FromMilliseconds(100), factor: 3.0); // 100ms, 300ms, 900ms, ...
BackoffStrategies.ExponentialWithJitter(TimeSpan.FromMilliseconds(100));    // exponential +/- 20% jitter
```

Or supply your own `Func<int, TimeSpan>` directly — attempt number is 1-based.

---

## Example: Circuit Breaker

`RetryAtom` composes directly with `SignalBasedCircuitBreaker`. The breaker reads the atom's own
`ExhaustedSignal` from the shared sink — no separate wiring needed.

```csharp
using Mostlylucid.Ephemeral.Atoms.Retry;
using Mostlylucid.Ephemeral.Patterns.CircuitBreaker;

var sink = new SignalSink();
var breaker = new SignalBasedCircuitBreaker(
    failureSignal: RetryAtom<ApiRequest>.ExhaustedSignal,
    threshold: 5,
    windowSize: TimeSpan.FromSeconds(30));

await using var atom = new RetryAtom<ApiRequest>(
    async (req, ct) => await CallExternalApi(req, ct),
    backoff: BackoffStrategies.Exponential(TimeSpan.FromMilliseconds(100)),
    maxTotalDuration: TimeSpan.FromSeconds(30),
    signals: sink,
    breaker: breaker);

try
{
    await atom.EnqueueAsync(request);
}
catch (CircuitOpenException ex)
{
    // Too many items have exhausted their retries recently; back off upstream instead
    // of piling more attempts onto a downstream that's already failing.
    log.Warn($"Circuit open, retry after {ex.RetryAfter}");
}
```

---

## Example: HTTP Calls

```csharp
await using var atom = new RetryAtom<HttpRequest>(
    async (req, ct) =>
    {
        var response = await httpClient.SendAsync(req.Message, ct);
        response.EnsureSuccessStatusCode();
    },
    backoff: BackoffStrategies.Exponential(TimeSpan.FromSeconds(1)),
    maxTotalDuration: TimeSpan.FromSeconds(30),
    maxAttempts: 3,
    maxConcurrency: 8);

foreach (var request in requests)
    await atom.EnqueueAsync(request);

await atom.DrainAsync();
```

---

## Related Packages

| Package                                                                                                                       | Description     |
|-------------------------------------------------------------------------------------------------------------------------------|-----------------|
| [mostlylucid.ephemeral](https://www.nuget.org/packages/mostlylucid.ephemeral)                                                 | Core library    |
| [mostlylucid.ephemeral.patterns.circuitbreaker](https://www.nuget.org/packages/mostlylucid.ephemeral.patterns.circuitbreaker) | Circuit breaker |
| [mostlylucid.ephemeral.complete](https://www.nuget.org/packages/mostlylucid.ephemeral.complete)                               | All in one DLL  |

## License

Unlicense (public domain)
