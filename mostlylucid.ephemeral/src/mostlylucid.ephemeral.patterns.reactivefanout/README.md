# Mostlylucid.Ephemeral.Patterns.ReactiveFanOut

[![NuGet](https://img.shields.io/nuget/v/mostlylucid.ephemeral.patterns.reactivefanout.svg)](https://www.nuget.org/packages/mostlylucid.ephemeral.patterns.reactivefanout)

Two-stage reactive pipeline that automatically throttles stage 1 when stage 2 signals backpressure.

```bash
dotnet add package mostlylucid.ephemeral.patterns.reactivefanout
```

## Quick Start

```csharp
using Mostlylucid.Ephemeral.Patterns.ReactiveFanOut;

await using var pipeline = new ReactiveFanOutPipeline<Message>(
    stage2Work: async (msg, ct) => await SaveToDbAsync(msg, ct),
    preStageWork: async (msg, ct) => await ValidateAsync(msg, ct),
    stage1MaxConcurrency: 16,
    stage1MinConcurrency: 2,
    stage2MaxConcurrency: 4,
    backpressureThreshold: 100);

await pipeline.EnqueueAsync(message);
await pipeline.DrainAsync();
```

---

## All Options

```csharp
new ReactiveFanOutPipeline<T>(
    // Required: stage 2 async work body
    stage2Work: async (item, ct) => await ProcessAsync(item, ct),

    // Optional: pre-stage work (runs in stage 1 before handoff)
    // Default: null (no-op)
    preStageWork: async (item, ct) => await ValidateAsync(item, ct),

    // Stage 1 max concurrency (scales down under pressure)
    // Default: 8
    stage1MaxConcurrency: 8,

    // Stage 1 min concurrency (floor when throttled)
    // Default: 1
    stage1MinConcurrency: 1,

    // Stage 2 max concurrency (fixed)
    // Default: 4
    stage2MaxConcurrency: 4,

    // Stage 2 pending count that triggers backpressure
    // Default: 32
    backpressureThreshold: 32,

    // Stage 2 pending count that clears backpressure
    // Default: 8
    reliefThreshold: 8,

    // Cooldown between concurrency adjustments (ms)
    // Default: 200
    adjustCooldownMs: 200,

    // Optional shared signal sink
    // Default: null (creates internal)
    sink: signalSink
)
```

---

## API Reference

```csharp
// Enqueue work item
await pipeline.EnqueueAsync(item, ct);

// Check current stage 1 concurrency
int stage1Concurrency = pipeline.Stage1CurrentMaxConcurrency;

// Check stage 2 pending count
int stage2Pending = pipeline.Stage2Pending;

// Drain both stages and dispose
await pipeline.DrainAsync(ct);
await pipeline.DisposeAsync();
```

---

## How It Works

```
Stage 1 (Validation/Transform)     Stage 2 (Slow I/O)
┌─────────────────────────────┐    ┌─────────────────┐
│ Max: 16, Min: 2             │───>│ Max: 4          │
│ Dynamic based on pressure   │    │ Fixed           │
└─────────────────────────────┘    └─────────────────┘
                                          │
                                          ▼
                              Pending > 32? ──> Throttle Stage 1
                              Pending < 8?  ──> Restore Stage 1
```

Signals emitted:
- `stage2.backpressure` - When stage 2 pending exceeds threshold
- `stage2.failed` - When stage 2 work fails

---

## Example: ETL Pipeline

```csharp
await using var pipeline = new ReactiveFanOutPipeline<Record>(
    stage2Work: async (record, ct) =>
    {
        // Slow database insert
        await database.InsertAsync(record, ct);
    },
    preStageWork: async (record, ct) =>
    {
        // Fast validation and transform
        await ValidateSchema(record, ct);
        record.Timestamp = DateTimeOffset.UtcNow;
    },
    stage1MaxConcurrency: 32,
    stage1MinConcurrency: 4,
    stage2MaxConcurrency: 8,
    backpressureThreshold: 200,
    reliefThreshold: 50);

// When DB slows down, Stage 1 throttles automatically
foreach (var record in records)
    await pipeline.EnqueueAsync(record);
```

---

## Example: Monitoring Pipeline State

```csharp
var sink = new SignalSink();

await using var pipeline = new ReactiveFanOutPipeline<Data>(
    stage2Work: ProcessDataAsync,
    sink: sink);

// Monitor in background
Task.Run(async () =>
{
    while (true)
    {
        Console.WriteLine($"Stage1 Concurrency: {pipeline.Stage1CurrentMaxConcurrency}");
        Console.WriteLine($"Stage2 Pending: {pipeline.Stage2Pending}");

        var backpressure = sink.Sense(s => s.Signal == "stage2.backpressure");
        if (backpressure.Any())
            Console.WriteLine("! Backpressure active");

        await Task.Delay(1000);
    }
});
```

---

## Related Packages

| Package | Description |
|---------|-------------|
| [mostlylucid.ephemeral](https://www.nuget.org/packages/mostlylucid.ephemeral) | Core library |
| [mostlylucid.ephemeral.patterns.backpressure](https://www.nuget.org/packages/mostlylucid.ephemeral.patterns.backpressure) | Simple backpressure |
| [mostlylucid.ephemeral.patterns.dynamicconcurrency](https://www.nuget.org/packages/mostlylucid.ephemeral.patterns.dynamicconcurrency) | Dynamic concurrency |
| [mostlylucid.ephemeral.complete](https://www.nuget.org/packages/mostlylucid.ephemeral.complete) | All in one DLL |

## License

MIT
