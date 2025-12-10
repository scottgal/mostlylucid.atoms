# Mostlylucid.Ephemeral.Atoms.SignalAware

[![NuGet](https://img.shields.io/nuget/v/mostlylucid.ephemeral.atoms.signalaware.svg)](https://www.nuget.org/packages/mostlylucid.ephemeral.atoms.signalaware)

> 🚨🚨 WARNING 🚨🚨 - Though in the 1.x range of version THINGS WILL STILL BREAK. This is the lab for developing this concept when stabilized it'll becoe the first *stylo*flow release 🚨🚨🚨


Pause or cancel intake based on ambient signals. Circuit-breaker and backpressure patterns.

```bash
dotnet add package mostlylucid.ephemeral.atoms.signalaware
```

## Quick Start

```csharp
using Mostlylucid.Ephemeral.Atoms.SignalAware;

var sink = new SignalSink();

await using var atom = new SignalAwareAtom<WorkItem>(
    async (item, ct) => await ProcessAsync(item, ct),
    cancelOn: new HashSet<string> { "shutdown" },
    deferOn: new HashSet<string> { "backpressure" },
    signals: sink);

await atom.EnqueueAsync(item);   // Normal processing

sink.Raise("backpressure");       // New items defer
sink.Raise("shutdown");           // New items rejected (-1)

await atom.DrainAsync();
```

---

## All Options

```csharp
new SignalAwareAtom<T>(
    // Required: async work body
    body: async (item, ct) => await ProcessAsync(item, ct),

    // Signals that reject items (returns -1)
    // Supports glob: "error.*", "circuit-*"
    // Default: null
    cancelOn: new HashSet<string> { "shutdown", "circuit-open" },

    // Signals that delay items until cleared
    // Supports glob patterns
    // Default: null
    deferOn: new HashSet<string> { "backpressure.*", "rate-limited" },

    // Recheck interval when deferring
    // Default: 100ms
    deferInterval: TimeSpan.FromMilliseconds(100),

    // Max defer attempts before running anyway
    // Default: 50
    maxDeferAttempts: 50,

    // Shared signal sink
    // Default: null
    signals: sharedSink,

    // Max concurrent operations
    // Default: Environment.ProcessorCount
    maxConcurrency: 8
)
```

---

## API Reference

```csharp
// Enqueue (returns -1 if canceled by signal)
ValueTask<long> id = await atom.EnqueueAsync(item, ct);

// Raise ambient signal
atom.Raise("backpressure");

// Drain and stats
await atom.DrainAsync(ct);
var snapshot = atom.Snapshot();
var (pending, active, completed, failed) = atom.Stats();

await atom.DisposeAsync();
```

---

## Example: Circuit Breaker

```csharp
var sink = new SignalSink();

await using var atom = new SignalAwareAtom<ApiRequest>(
    async (req, ct) =>
    {
        try { await CallApi(req, ct); }
        catch { sink.Raise("api.failure"); throw; }
    },
    cancelOn: new HashSet<string> { "circuit-open" },
    signals: sink);

// Monitor and open circuit
Task.Run(async () =>
{
    while (true)
    {
        if (sink.Sense(s => s.Signal == "api.failure").Count() > 5)
            sink.Raise("circuit-open");
        await Task.Delay(1000);
    }
});
```

---

## Example: Backpressure

```csharp
var sink = new SignalSink();

await using var atom = new SignalAwareAtom<WorkItem>(
    async (item, ct) => await SlowProcess(item, ct),
    deferOn: new HashSet<string> { "backpressure.*" },
    signals: sink);

sink.Raise("backpressure.downstream");  // Items wait
sink.Retract("backpressure.downstream"); // Items resume
```

---

## Related Packages

| Package                                                                                                                       | Description          |
|-------------------------------------------------------------------------------------------------------------------------------|----------------------|
| [mostlylucid.ephemeral](https://www.nuget.org/packages/mostlylucid.ephemeral)                                                 | Core library         |
| [mostlylucid.ephemeral.patterns.circuitbreaker](https://www.nuget.org/packages/mostlylucid.ephemeral.patterns.circuitbreaker) | Full circuit breaker |
| [mostlylucid.ephemeral.complete](https://www.nuget.org/packages/mostlylucid.ephemeral.complete)                               | All in one DLL       |

## License

Unlicense (public domain)