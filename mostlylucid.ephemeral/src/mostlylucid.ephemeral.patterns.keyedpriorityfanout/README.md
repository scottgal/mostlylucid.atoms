# Mostlylucid.Ephemeral.Patterns.KeyedPriorityFanOut

[![NuGet](https://img.shields.io/nuget/v/mostlylucid.ephemeral.patterns.keyedpriorityfanout.svg)](https://www.nuget.org/packages/mostlylucid.ephemeral.patterns.keyedpriorityfanout)

Keyed fan-out with multiple priority lanes - priority items drain first while maintaining per-key ordering.

```bash
dotnet add package mostlylucid.ephemeral.patterns.keyedpriorityfanout
```

## Quick Start

```csharp
using Mostlylucid.Ephemeral.Patterns.KeyedPriorityFanOut;

await using var fan = new KeyedPriorityFanOut<string, UserCommand>(
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

---

## All Options

```csharp
new KeyedPriorityFanOut<TKey, T>(
    // Required: extract key from item
    keySelector: item => item.Key,

    // Required: async work body
    body: async (item, ct) => await ProcessAsync(item, ct),

    // Max concurrent operations across all keys
    maxConcurrency: 32,

    // Max concurrent operations per key
    // Default: 1 (sequential per key)
    perKeyConcurrency: 1,

    // Optional shared signal sink
    // Default: null
    sink: signalSink,

    // Max items in priority queue (null = unlimited)
    // Default: null
    maxPriorityDepth: 100,

    // Signals that reject priority items
    // Default: null
    cancelPriorityOn: new HashSet<string> { "circuit.open" },

    // Signals that defer priority items
    // Default: null
    deferPriorityOn: new HashSet<string> { "backpressure" }
)
```

---

## API Reference

```csharp
// Enqueue to normal lane
await fan.EnqueueAsync(item, ct);

// Enqueue to priority lane (returns false if rejected)
bool accepted = await fan.EnqueuePriorityAsync(item, ct);

// Get pending counts for both lanes
LaneCounts counts = fan.PendingCounts; // (Priority, Normal)

// Drain and dispose
await fan.DrainAsync(ct);
await fan.DisposeAsync();
```

---

## How It Works

```
Priority Lane: [urgent1] [urgent2]  <- Drains first
               ────────────────────
Normal Lane:   [item1] [item2] [item3] [item4]
               ──────────────────────────────────

Per-key ordering preserved within each lane:
  User-A Priority: [cmd1] -> [cmd2]    (sequential)
  User-A Normal:   [cmd3] -> [cmd4]    (sequential, after priority)
  User-B Priority: [cmd1]              (parallel with User-A)
```

---

## Example: VIP Order Processing

```csharp
await using var fan = new KeyedPriorityFanOut<string, Order>(
    keySelector: order => order.CustomerId,
    body: async (order, ct) =>
    {
        await ValidateOrder(order, ct);
        await ProcessPayment(order, ct);
        await FulfillOrder(order, ct);
    },
    maxConcurrency: 16,
    perKeyConcurrency: 1,
    maxPriorityDepth: 50);

foreach (var order in incomingOrders)
{
    if (order.IsVIP)
        await fan.EnqueuePriorityAsync(order);
    else
        await fan.EnqueueAsync(order);
}
```

---

## Example: Circuit Breaker Integration

```csharp
var sink = new SignalSink();

await using var fan = new KeyedPriorityFanOut<string, Request>(
    keySelector: req => req.ServiceId,
    body: ProcessRequestAsync,
    maxConcurrency: 32,
    cancelPriorityOn: new HashSet<string> { "circuit.open" },
    deferPriorityOn: new HashSet<string> { "backpressure.*" },
    sink: sink);

// When circuit opens, priority items are rejected
sink.Raise("circuit.open");
var accepted = await fan.EnqueuePriorityAsync(request); // false

// When backpressure, priority items defer
sink.Raise("backpressure.downstream");
// Priority items wait until signal clears
```

---

## Related Packages

| Package | Description |
|---------|-------------|
| [mostlylucid.ephemeral](https://www.nuget.org/packages/mostlylucid.ephemeral) | Core library |
| [mostlylucid.ephemeral.patterns.controlledfanout](https://www.nuget.org/packages/mostlylucid.ephemeral.patterns.controlledfanout) | Controlled fan-out |
| [mostlylucid.ephemeral.atoms.keyedsequential](https://www.nuget.org/packages/mostlylucid.ephemeral.atoms.keyedsequential) | Keyed sequential atom |
| [mostlylucid.ephemeral.complete](https://www.nuget.org/packages/mostlylucid.ephemeral.complete) | All in one DLL |

## License

MIT
