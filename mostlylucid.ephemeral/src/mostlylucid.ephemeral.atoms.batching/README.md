# Mostlylucid.Ephemeral.Atoms.Batching

[![NuGet](https://img.shields.io/nuget/v/mostlylucid.ephemeral.atoms.batching.svg)](https://www.nuget.org/packages/mostlylucid.ephemeral.atoms.batching)

Collect items into batches by size or time interval before processing.

```bash
dotnet add package mostlylucid.ephemeral.atoms.batching
```

## Quick Start

```csharp
using Mostlylucid.Ephemeral.Atoms.Batching;

await using var atom = new BatchingAtom<LogEntry>(
    onBatch: async (batch, ct) => await FlushToDatabase(batch, ct),
    maxBatchSize: 100,
    flushInterval: TimeSpan.FromSeconds(5));

// Items batched automatically
atom.Enqueue(new LogEntry("User logged in"));
atom.Enqueue(new LogEntry("Request received"));
// Flushes when full OR after 5 seconds
```

---

## All Options

```csharp
new BatchingAtom<T>(
    // Required: async batch processor
    onBatch: async (batch, ct) => await ProcessBatch(batch, ct),

    // Flush when batch reaches this size
    // Default: 32
    maxBatchSize: 100,

    // Flush after this interval even if not full
    // Default: 1 second
    flushInterval: TimeSpan.FromSeconds(5)
)
```

---

## API Reference

```csharp
// Add item to batch (non-blocking, synchronous)
atom.Enqueue(item);

// Dispose - flushes remaining items
await atom.DisposeAsync();
```

---

## Flush Behavior

Flushes when **either** condition is met:

- **Size**: Batch reaches `maxBatchSize`
- **Time**: `flushInterval` elapses

```
[1] [2] [3] ... [100] -> FLUSH (size)
[1] [2] [3] (5s pass) -> FLUSH (time)
```

---

## Example: Log Aggregation

```csharp
await using var atom = new BatchingAtom<LogEntry>(
    onBatch: async (batch, ct) =>
    {
        Console.WriteLine($"Flushing {batch.Count} entries");
        await database.BulkInsertAsync(batch, ct);
    },
    maxBatchSize: 500,
    flushInterval: TimeSpan.FromSeconds(10));

foreach (var entry in incomingLogs)
    atom.Enqueue(entry);  // Non-blocking
```

---

## Example: Metrics

```csharp
await using var atom = new BatchingAtom<Metric>(
    onBatch: async (batch, ct) =>
    {
        var aggregated = batch
            .GroupBy(m => m.Name)
            .Select(g => new { Name = g.Key, Avg = g.Average(m => m.Value) });
        await metricsService.ReportAsync(aggregated, ct);
    },
    maxBatchSize: 1000,
    flushInterval: TimeSpan.FromMinutes(1));

atom.Enqueue(new Metric("response_time", 42.5));
```

---

## Related Packages

| Package                                                                                         | Description    |
|-------------------------------------------------------------------------------------------------|----------------|
| [mostlylucid.ephemeral](https://www.nuget.org/packages/mostlylucid.ephemeral)                   | Core library   |
| [mostlylucid.ephemeral.complete](https://www.nuget.org/packages/mostlylucid.ephemeral.complete) | All in one DLL |

## License

Unlicense (public domain)
