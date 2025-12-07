# Mostlylucid.Ephemeral.Patterns.DynamicConcurrency

Dynamic concurrency adjustment based on load signals.

## How It Works

- When `load.high` signal appears: double concurrency (up to max)
- When `load.low` signal appears: halve concurrency (down to min)

## Usage

```csharp
var sink = new SignalSink();
var demo = new DynamicConcurrencyDemo<WorkItem>(
    async (item, ct) => await ProcessAsync(item, ct),
    sink,
    minConcurrency: 2,
    maxConcurrency: 64);

// External system raises load signals
sink.Raise("load.high");  // Doubles concurrency
sink.Raise("load.low");   // Halves concurrency
```

## License

MIT
