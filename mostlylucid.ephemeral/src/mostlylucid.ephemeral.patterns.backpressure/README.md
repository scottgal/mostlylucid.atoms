# Mostlylucid.Ephemeral.Patterns.Backpressure

[![NuGet](https://img.shields.io/nuget/v/mostlylucid.ephemeral.patterns.backpressure.svg)](https://www.nuget.org/packages/mostlylucid.ephemeral.patterns.backpressure)

> 🚨🚨 WARNING 🚨🚨 - Though in the 1.x range of version THINGS WILL STILL BREAK. This is the lab for developing this concept when stabilized it'll becoe the first *stylo*flow release 🚨🚨🚨


Signal-driven backpressure - defer intake when backpressure signals present.

```bash
dotnet add package mostlylucid.ephemeral.patterns.backpressure
```

## Quick Start

```csharp
using Mostlylucid.Ephemeral.Patterns.Backpressure;

var sink = new SignalSink();

var coordinator = SignalDrivenBackpressure.Create<WorkItem>(
    async (item, ct) => await ProcessAsync(item, ct),
    sink,
    maxConcurrency: 4);

sink.Raise("backpressure.downstream");  // New work defers
await coordinator.EnqueueAsync(item);    // Waits until signal clears
sink.Retract("backpressure.downstream"); // Work resumes
```

---

## All Options

```csharp
SignalDrivenBackpressure.Create<T>(
    // Required: async work body
    body: async (item, ct) => await ProcessAsync(item, ct),

    // Required: shared signal sink
    sink: signalSink,

    // Max concurrent operations
    // Default: 4
    maxConcurrency: 4
)
```

---

## API Reference

```csharp
// Returns a configured EphemeralWorkCoordinator<T>
var coordinator = SignalDrivenBackpressure.Create<T>(body, sink, maxConcurrency);

// Enqueue work (defers if backpressure.* signal present)
await coordinator.EnqueueAsync(item);

// Drain and dispose
coordinator.Complete();
await coordinator.DrainAsync();
await coordinator.DisposeAsync();
```

---

## How It Works

Items automatically defer when any signal matching `backpressure.*` is present:

```
sink.Raise("backpressure.downstream")
  │
  ▼
EnqueueAsync(item) ──> [Defer] ──> Wait 50ms ──> Check signals ──> [Still present] ──> Wait...
                                                        │
                                                        ▼
                                            [Signal cleared] ──> Process item
```

---

## Example: Downstream Throttling

```csharp
var sink = new SignalSink();

await using var coordinator = SignalDrivenBackpressure.Create<Message>(
    async (msg, ct) =>
    {
        await downstream.SendAsync(msg, ct);
    },
    sink,
    maxConcurrency: 8);

// Downstream service reports it's overloaded
sink.Raise("backpressure.downstream");

// New messages defer until downstream recovers
foreach (var msg in messages)
    await coordinator.EnqueueAsync(msg);

// Downstream recovers
sink.Retract("backpressure.downstream");
// All deferred work resumes
```

---

## Configuration Details

The pattern internally configures:

```csharp
new EphemeralOptions
{
    MaxConcurrency = maxConcurrency,
    Signals = sink,
    DeferOnSignals = new HashSet<string> { "backpressure.*" },
    DeferCheckInterval = TimeSpan.FromMilliseconds(50),
    MaxDeferAttempts = 200
}
```

---

## Related Packages

| Package                                                                                                                       | Description               |
|-------------------------------------------------------------------------------------------------------------------------------|---------------------------|
| [mostlylucid.ephemeral](https://www.nuget.org/packages/mostlylucid.ephemeral)                                                 | Core library              |
| [mostlylucid.ephemeral.patterns.reactivefanout](https://www.nuget.org/packages/mostlylucid.ephemeral.patterns.reactivefanout) | Reactive fan-out pipeline |
| [mostlylucid.ephemeral.atoms.signalaware](https://www.nuget.org/packages/mostlylucid.ephemeral.atoms.signalaware)             | Signal-aware atom         |
| [mostlylucid.ephemeral.complete](https://www.nuget.org/packages/mostlylucid.ephemeral.complete)                               | All in one DLL            |

## License

Unlicense (public domain)