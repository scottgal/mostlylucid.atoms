# Mostlylucid.Ephemeral.Patterns.SignalReactionShowcase

Demonstrates signal emission, async dispatch, and sink polling patterns.

## How It Works

- Work items emit signals during execution
- SignalDispatcher handles signals asynchronously off the hot path
- SignalSink collects signals for polling/querying

## Usage

```csharp
var result = await SignalReactionShowcase.RunAsync(itemCount: 100);

Console.WriteLine($"Dispatched: {result.DispatchedHits}");
Console.WriteLine($"Polled: {result.PolledHits}");
Console.WriteLine($"Total signals: {result.Signals.Count}");
```

## Key Concepts

1. **Signal Emission**: Work items raise signals synchronously (fast)
2. **Async Dispatch**: SignalDispatcher processes signals in background
3. **Sink Polling**: Query signals by pattern after work completes

## License

MIT
