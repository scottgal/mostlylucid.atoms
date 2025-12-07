# Mostlylucid.Ephemeral.Patterns.SignalCoordinatedReads

Signal-coordinated readers that pause during updates - quiesce reads without hard locks.

## How It Works

- Readers defer when `update.in-progress` signal is present
- Updater emits signal, performs work, then clears it
- No hard locks - just cooperative signal-based coordination

## Usage

```csharp
var result = await SignalCoordinatedReads.RunAsync(
    readCount: 100,
    updateCount: 5);

Console.WriteLine($"Reads: {result.ReadsCompleted}, Updates: {result.UpdatesCompleted}");
```

## Use Cases

- Config reloads without blocking readers permanently
- Database migrations with graceful read pauses
- Cache invalidation coordination

## License

MIT
