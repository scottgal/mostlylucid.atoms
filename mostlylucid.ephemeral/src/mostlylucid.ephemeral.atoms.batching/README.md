# Mostlylucid.Ephemeral.Atoms.Batching

Collects items into batches and processes them when full or when a time interval elapses.

## Features

- **Size-based batching**: Process when batch reaches max size
- **Time-based flushing**: Flush partial batches after interval
- **Non-blocking enqueue**: Returns immediately, batching is background

## Usage

```csharp
await using var atom = new BatchingAtom<LogEntry>(
    async (batch, ct) => await WriteBatchAsync(batch, ct),
    maxBatchSize: 100,
    flushInterval: TimeSpan.FromSeconds(5));

// Enqueue items - returns immediately
atom.Enqueue(new LogEntry("Event 1"));
atom.Enqueue(new LogEntry("Event 2"));
// ... more events

// Batch is processed when:
// - 100 items accumulated, OR
// - 5 seconds elapsed since last flush
```

## API

| Parameter | Description |
|-----------|-------------|
| `onBatch` | Async function to process each batch |
| `maxBatchSize` | Max items before auto-flush (default: 32) |
| `flushInterval` | Time interval for partial flush (default: 1s) |

## Use Cases

- Log aggregation before writing to storage
- Event coalescing for analytics
- Bulk database inserts

## License

MIT
