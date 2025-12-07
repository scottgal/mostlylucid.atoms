# Mostlylucid.Ephemeral.Patterns.KeyedPriorityFanOut

Keyed fan-out with multiple priority lanes - priority items drain first while maintaining per-key ordering.

## How It Works

- Two lanes: "priority" and "normal"
- Priority items always drain before normal items
- Per-key ordering is preserved within each lane
- Optional depth limits and signal-based gating on priority lane

## Usage

```csharp
var fan = new KeyedPriorityFanOut<string, UserCommand>(
    keySelector: cmd => cmd.UserId,
    body: HandleCommandAsync,
    maxConcurrency: 32,
    perKeyConcurrency: 1);

// Normal priority
await fan.EnqueueAsync(command);

// High priority - jumps ahead for this user
var accepted = await fan.EnqueuePriorityAsync(urgentCommand);

// Check queue depths
var counts = fan.PendingCounts;  // (Priority: 0, Normal: 5)
```

## Advanced Configuration

```csharp
var fan = new KeyedPriorityFanOut<string, Command>(
    keySelector: cmd => cmd.Key,
    body: ProcessAsync,
    maxConcurrency: 16,
    maxPriorityDepth: 100,           // Limit priority queue size
    cancelPriorityOn: new HashSet<string> { "circuit.open" },
    deferPriorityOn: new HashSet<string> { "backpressure" });
```

## License

MIT
