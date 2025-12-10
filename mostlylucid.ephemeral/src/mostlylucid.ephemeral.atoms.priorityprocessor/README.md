# Mostlylucid.Ephemeral.Atoms.PriorityProcessor

[![NuGet](https://img.shields.io/nuget/v/mostlylucid.ephemeral.atoms.priorityprocessor.svg)](https://www.nuget.org/packages/mostlylucid.ephemeral.atoms.priorityprocessor)

> 🚨🚨 WARNING 🚨🚨 - Though in the 1.x range of version THINGS WILL STILL BREAK. This is the lab for developing this concept when stabilized it'll become the first *stylo*flow release 🚨🚨🚨

Priority-based processing with automatic failover, health monitoring, and self-healing recovery.

```bash
dotnet add package mostlylucid.ephemeral.atoms.priorityprocessor
```

## Quick Start

```csharp
using Mostlylucid.Ephemeral.Atoms.PriorityProcessor;

var sink = new SignalSink();

// Priority 1 (primary) processor - handles most work
await using var primary = new PriorityProcessorAtom(
    priority: 1,
    processFunc: async (id, ct) => await ProcessWidget(id, ct),
    signals: sink,
    failureThreshold: 3);

// Priority 2 (backup) processor - takes over on failures
await using var backup = new PriorityProcessorAtom(
    priority: 2,
    processFunc: async (id, ct) => await ProcessWidgetBackup(id, ct),
    signals: sink);

// Enqueue work - automatically routes based on health
await primary.EnqueueAsync("widget-123");

// Primary fails 3 times → automatic failover to backup
// Periodic probing restores primary when healthy

await primary.DrainAsync();
await backup.DrainAsync();
```

---

## Key Features

### 🔄 Automatic Failover
Primary processor fails repeatedly? Backup processor instantly takes over based on signal-driven health checks.

### 🏥 Self-Healing
Periodic probing automatically detects when primary processor recovers and restores routing.

### 📊 Full Observability
All decisions captured as signals:
- `processing.started:pri{N}:{id}`
- `processing.complete:pri{N}:{id}`
- `processing.failed:pri{N}:{id}`
- `failover.requested:pri{N}→pri{N+1}`
- `probe.testing:pri{N}`
- `probe.success:pri{N}`

### ⚡ Zero Overhead
Signal-based coordination with lock-free listener arrays - no polling, no timers during normal operation.

---

## All Options

```csharp
new PriorityProcessorAtom(
    // Required: priority level (1 = highest)
    priority: 1,

    // Required: async processing function
    processFunc: async (itemId, ct) => await Process(itemId, ct),

    // Required: shared signal sink
    signals: sink,

    // Consecutive failures before marking unhealthy
    // Default: 3
    failureThreshold: 3,

    // Max concurrent operations
    // Default: Environment.ProcessorCount
    maxConcurrency: 4,

    // Time window for failure counting
    // Default: 10 seconds
    failureWindow: TimeSpan.FromSeconds(10),

    // Probe interval when unhealthy
    // Default: 5 seconds
    probeInterval: TimeSpan.FromSeconds(5)
)
```

---

## Pattern: Dynamic Adaptive Workflow

```csharp
var globalSink = new SignalSink(maxCapacity: 5000);
var processorHealth = new ConcurrentDictionary<int, bool> { [1] = true, [2] = true };

// Primary processor (30% failure rate for demo)
await using var processor1 = new EphemeralWorkCoordinator<string>(
    async (widgetId, ct) =>
    {
        globalSink.Raise($"processing.started:pri1:{widgetId}");
        await Task.Delay(Random.Shared.Next(30, 80), ct);
        var success = Random.Shared.NextDouble() > 0.3;

        if (success)
        {
            processorHealth[1] = true;
            globalSink.Raise($"processing.complete:pri1:{widgetId}");
        }
        else
        {
            globalSink.Raise($"processing.failed:pri1:{widgetId}");

            // OPTIMIZED health check using CountRecentByPrefix
            var recentFailures = globalSink.CountRecentByPrefix(
                "processing.failed:pri1",
                DateTimeOffset.UtcNow.AddSeconds(-10));

            if (recentFailures >= 3 && processorHealth[1])
            {
                processorHealth[1] = false;
                globalSink.Raise("failover.triggered:pri1→pri2");
            }
        }
    },
    new EphemeralOptions { MaxConcurrency = 4, Signals = globalSink }
);

// Backup processor (5% failure rate, reliable)
await using var processor2 = new EphemeralWorkCoordinator<string>(
    async (widgetId, ct) =>
    {
        globalSink.Raise($"processing.started:pri2:{widgetId}");
        await Task.Delay(Random.Shared.Next(50, 120), ct);
        var success = Random.Shared.NextDouble() > 0.05;

        if (success)
        {
            globalSink.Raise($"processing.complete:pri2:{widgetId}");
        }
        else
        {
            globalSink.Raise($"processing.failed:pri2:{widgetId}");
        }
    },
    new EphemeralOptions { MaxConcurrency = 4, Signals = globalSink }
);

// Router coordinator - dynamic routing based on health
await using var router = new EphemeralWorkCoordinator<string>(
    async (widgetId, ct) =>
    {
        var targetPriority = processorHealth[1] ? 1 : 2;
        globalSink.Raise($"route.assigned:pri{targetPriority}:{widgetId}");

        if (targetPriority == 1)
            await processor1.EnqueueAsync(widgetId);
        else
            await processor2.EnqueueAsync(widgetId);
    },
    new EphemeralOptions { MaxConcurrency = 16, Signals = globalSink }
);

// Self-healing prober
var proberCts = new CancellationTokenSource();
var probeTask = Task.Run(async () =>
{
    while (!proberCts.Token.IsCancellationRequested)
    {
        await Task.Delay(3000, proberCts.Token);

        if (!processorHealth[1])
        {
            globalSink.Raise("probe.testing:pri1");

            var recovered = await TestProcessor1Health();
            if (recovered)
            {
                globalSink.Raise("probe.success:pri1");
                processorHealth[1] = true;
            }
        }
    }
}, proberCts.Token);

// Process widgets
for (int i = 0; i < 100; i++)
{
    await router.EnqueueAsync($"WIDGET-{i}");
}

// Cleanup
router.Complete();
processor1.Complete();
processor2.Complete();

await router.DrainAsync();
await processor1.DrainAsync();
await processor2.DrainAsync();

proberCts.Cancel();
```

---

## Performance

**Optimized Signal Queries** - Uses `CountRecentByPrefix()` for 70% faster health checks:

- **Before (LINQ + Sense)**: 94.49µs per query | 10,583 queries/sec
- **After (CountRecentByPrefix)**: 55.57µs per query | 17,994 queries/sec

**Signal Raising** - Lock-free performance:
- 790,000+ signals/sec single-threaded
- 850,000+ signals/sec with 4 concurrent threads

**Failover Latency** - <10ms from failure detection to routing change

---

## Use Cases

### Multi-Tenant SaaS
Route customer requests to healthy region-specific processors with automatic failover.

### Edge Computing
Prioritize on-device processing, fall back to cloud with self-healing when device recovers.

### Financial Processing
Primary fast-path validation, backup comprehensive audit with automatic recovery.

### IoT Data Pipelines
Route sensor data to primary aggregator, failover to backup on network issues.

### Microservices
Service mesh routing with health-based failover and recovery.

---

## Related Packages

| Package                                                                                                                       | Description        |
|-------------------------------------------------------------------------------------------------------------------------------|--------------------|
| [mostlylucid.ephemeral](https://www.nuget.org/packages/mostlylucid.ephemeral)                                                 | Core library       |
| [mostlylucid.ephemeral.atoms.retry](https://www.nuget.org/packages/mostlylucid.ephemeral.atoms.retry)                         | Retry with backoff |
| [mostlylucid.ephemeral.patterns.circuitbreaker](https://www.nuget.org/packages/mostlylucid.ephemeral.patterns.circuitbreaker) | Circuit breaker    |
| [mostlylucid.ephemeral.complete](https://www.nuget.org/packages/mostlylucid.ephemeral.complete)                               | All in one DLL     |

## License

Unlicense (public domain)
