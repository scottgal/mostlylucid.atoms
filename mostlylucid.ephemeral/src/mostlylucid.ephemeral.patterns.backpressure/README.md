# Mostlylucid.Ephemeral.Patterns.Backpressure

Signal-driven backpressure pattern - defer intake when backpressure signals present.

## Usage

```csharp
var sink = new SignalSink();
var coordinator = SignalDrivenBackpressure.Create<WorkItem>(
    async (item, ct) => await ProcessAsync(item, ct),
    sink,
    maxConcurrency: 4);

// Downstream raises signal when overwhelmed
sink.Raise("backpressure.downstream");

// New work automatically defers until signal ages out
await coordinator.EnqueueAsync(item);
```

## License

MIT
