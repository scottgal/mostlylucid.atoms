# Mostlylucid.Ephemeral.Patterns.DynamicConcurrency

[![NuGet](https://img.shields.io/nuget/v/mostlylucid.ephemeral.patterns.dynamicconcurrency.svg)](https://www.nuget.org/packages/mostlylucid.ephemeral.patterns.dynamicconcurrency)

Dynamic concurrency adjustment based on load signals.

```bash
dotnet add package mostlylucid.ephemeral.patterns.dynamicconcurrency
```

## Quick Start

```csharp
using Mostlylucid.Ephemeral.Patterns.DynamicConcurrency;

var sink = new SignalSink();

await using var demo = new DynamicConcurrencyDemo<WorkItem>(
    async (item, ct) => await ProcessAsync(item, ct),
    sink,
    minConcurrency: 2,
    maxConcurrency: 64);

// External system raises load signals
sink.Raise("load.high");  // Doubles concurrency
sink.Raise("load.low");   // Halves concurrency
```

---

## All Options

```csharp
new DynamicConcurrencyDemo<T>(
    // Required: async work body
    body: async (item, ct) => await ProcessAsync(item, ct),

    // Required: shared signal sink
    sink: signalSink,

    // Minimum concurrency level
    // Default: 1
    minConcurrency: 1,

    // Maximum concurrency level
    // Default: 32
    maxConcurrency: 32,

    // Signal pattern to trigger scale up
    // Default: "load.high"
    scaleUpPattern: "load.high",

    // Signal pattern to trigger scale down
    // Default: "load.low"
    scaleDownPattern: "load.low"
)
```

---

## API Reference

```csharp
// Enqueue work item
await demo.EnqueueAsync(item, ct);

// Check current concurrency level
int current = demo.CurrentMaxConcurrency;

// Drain and dispose
await demo.DrainAsync(ct);
await demo.DisposeAsync();
```

---

## How It Works

A background loop monitors signals and adjusts concurrency:

```
Signal "load.high" detected:
    Current: 4 -> Next: min(8, max) = 8  (doubled)

Signal "load.low" detected:
    Current: 8 -> Next: max(4, min) = 4  (halved)

Check interval: 200ms
```

---

## Example: Auto-Scaling Worker

```csharp
var sink = new SignalSink();

await using var worker = new DynamicConcurrencyDemo<Job>(
    async (job, ct) => await ProcessJob(job, ct),
    sink,
    minConcurrency: 2,
    maxConcurrency: 64);

// Monitor system metrics and raise signals
Task.Run(async () =>
{
    while (true)
    {
        var metrics = await GetSystemMetrics();

        if (metrics.CpuUsage < 30 && metrics.QueueDepth > 1000)
            sink.Raise("load.high");  // Scale up
        else if (metrics.CpuUsage > 80 || metrics.QueueDepth < 10)
            sink.Raise("load.low");   // Scale down

        await Task.Delay(1000);
    }
});

foreach (var job in jobs)
    await worker.EnqueueAsync(job);
```

---

## Example: Custom Signal Patterns

```csharp
await using var worker = new DynamicConcurrencyDemo<Task>(
    ProcessTaskAsync,
    sink,
    scaleUpPattern: "scale.up.*",    // Match scale.up.cpu, scale.up.queue, etc.
    scaleDownPattern: "scale.down.*");

sink.Raise("scale.up.queue");    // Triggers scale up
sink.Raise("scale.down.memory"); // Triggers scale down
```

---

## Related Packages

| Package | Description |
|---------|-------------|
| [mostlylucid.ephemeral](https://www.nuget.org/packages/mostlylucid.ephemeral) | Core library |
| [mostlylucid.ephemeral.patterns.adaptiverate](https://www.nuget.org/packages/mostlylucid.ephemeral.patterns.adaptiverate) | Adaptive rate limiting |
| [mostlylucid.ephemeral.patterns.reactivefanout](https://www.nuget.org/packages/mostlylucid.ephemeral.patterns.reactivefanout) | Reactive fan-out |
| [mostlylucid.ephemeral.complete](https://www.nuget.org/packages/mostlylucid.ephemeral.complete) | All in one DLL |

## License

MIT
