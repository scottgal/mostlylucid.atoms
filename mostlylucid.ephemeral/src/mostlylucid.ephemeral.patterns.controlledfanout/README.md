# Mostlylucid.Ephemeral.Patterns.ControlledFanOut

[![NuGet](https://img.shields.io/nuget/v/mostlylucid.ephemeral.patterns.controlledfanout.svg)](https://www.nuget.org/packages/mostlylucid.ephemeral.patterns.controlledfanout)

Global gate bounds total concurrency while per-key ordering is preserved.

```bash
dotnet add package mostlylucid.ephemeral.patterns.controlledfanout
```

## Quick Start

```csharp
using Mostlylucid.Ephemeral.Patterns.ControlledFanOut;

await using var fanout = new ControlledFanOut<string, Message>(
    msg => msg.UserId,
    async (msg, ct) => await ProcessAsync(msg, ct),
    maxGlobalConcurrency: 16,
    perKeyConcurrency: 1);

await fanout.EnqueueAsync(message);
await fanout.DrainAsync();
```

---

## All Options

```csharp
new ControlledFanOut<TKey, T>(
    // Required: extract key from item
    keySelector: item => item.Key,

    // Required: async work body
    body: async (item, ct) => await ProcessAsync(item, ct),

    // Max concurrent operations across all keys
    maxGlobalConcurrency: 16,

    // Max concurrent operations per key
    // Default: 1 (sequential per key)
    perKeyConcurrency: 1,

    // Optional shared signal sink
    // Default: null
    sink: signalSink
)
```

---

## API Reference

```csharp
// Enqueue work item
await fanout.EnqueueAsync(item, ct);

// Stop accepting and drain
await fanout.DrainAsync(ct);

// Dispose
await fanout.DisposeAsync();
```

---

## How It Works

```
Global Gate: 16 concurrent
    │
    ├── Key "user-A": [msg1] -> [msg2] -> [msg3]  (sequential)
    │
    ├── Key "user-B": [msg1] -> [msg2]            (sequential)
    │
    └── Key "user-C": [msg1]                      (sequential)

All keys process in parallel, but items within each key are sequential.
Total active operations never exceed 16.
```

---

## Example: Order Processing

```csharp
await using var fanout = new ControlledFanOut<string, Order>(
    order => order.CustomerId,
    async (order, ct) =>
    {
        await ValidateInventory(order, ct);
        await ChargePayment(order, ct);
        await ShipOrder(order, ct);
    },
    maxGlobalConcurrency: 32,
    perKeyConcurrency: 1);

// Customer A's orders: sequential
// Customer B's orders: sequential
// A and B: parallel (up to 32 total)
foreach (var order in incomingOrders)
    await fanout.EnqueueAsync(order);

await fanout.DrainAsync();
```

---

## Example: With Signal Sink

```csharp
var sink = new SignalSink();

await using var fanout = new ControlledFanOut<string, Message>(
    msg => msg.UserId,
    async (msg, ct) =>
    {
        try
        {
            await ProcessMessage(msg, ct);
        }
        catch
        {
            sink.Raise($"error.user.{msg.UserId}");
            throw;
        }
    },
    maxGlobalConcurrency: 16,
    sink: sink);

// Monitor errors by user
var userErrors = sink.Sense(s => s.Signal.StartsWith("error.user."));
```

---

## Related Packages

| Package | Description |
|---------|-------------|
| [mostlylucid.ephemeral](https://www.nuget.org/packages/mostlylucid.ephemeral) | Core library |
| [mostlylucid.ephemeral.patterns.keyedpriorityfanout](https://www.nuget.org/packages/mostlylucid.ephemeral.patterns.keyedpriorityfanout) | Priority lanes |
| [mostlylucid.ephemeral.atoms.keyedsequential](https://www.nuget.org/packages/mostlylucid.ephemeral.atoms.keyedsequential) | Keyed sequential atom |
| [mostlylucid.ephemeral.complete](https://www.nuget.org/packages/mostlylucid.ephemeral.complete) | All in one DLL |

## License

Unlicense (public domain)
