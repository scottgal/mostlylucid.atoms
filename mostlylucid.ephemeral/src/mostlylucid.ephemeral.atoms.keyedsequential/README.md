# Mostlylucid.Ephemeral.Atoms.KeyedSequential

[![NuGet](https://img.shields.io/nuget/v/mostlylucid.ephemeral.atoms.keyedsequential.svg)](https://www.nuget.org/packages/mostlylucid.ephemeral.atoms.keyedsequential)

Per-key sequential processing. Items with the same key are processed in order.

```bash
dotnet add package mostlylucid.ephemeral.atoms.keyedsequential
```

## Quick Start

```csharp
using Mostlylucid.Ephemeral.Atoms.KeyedSequential;

await using var atom = new KeyedSequentialAtom<Order, string>(
    keySelector: order => order.CustomerId,
    body: async (order, ct) => await ProcessOrder(order, ct));

await atom.EnqueueAsync(order1);  // Customer A
await atom.EnqueueAsync(order2);  // Customer A - waits for order1
await atom.EnqueueAsync(order3);  // Customer B - parallel with A

await atom.DrainAsync();
```

---

## All Options

```csharp
new KeyedSequentialAtom<T, TKey>(
    // Required: extract key from item
    keySelector: item => item.Key,

    // Required: async work body
    body: async (item, ct) => await ProcessAsync(item, ct),

    // Max concurrent operations across all keys
    // Default: Environment.ProcessorCount
    maxConcurrency: 16,

    // Max concurrent operations per key
    // Default: 1 (sequential per key)
    perKeyConcurrency: 1,

    // Prevent hot keys from starving cold keys
    // Default: false
    enableFairScheduling: true,

    // Shared signal sink
    // Default: null (isolated)
    signals: sharedSink
)
```

---

## API Reference

```csharp
// Enqueue work item, returns operation ID
ValueTask<long> id = await atom.EnqueueAsync(item, ct);

// Stop accepting work and wait for completion
await atom.DrainAsync(ct);

// Get recent operations snapshot
IReadOnlyCollection<EphemeralOperationSnapshot> snapshot = atom.Snapshot();

// Get aggregate stats
var (pending, active, completed, failed) = atom.Stats();

// Dispose
await atom.DisposeAsync();
```

---

## Example: Order Processing

```csharp
await using var atom = new KeyedSequentialAtom<Order, string>(
    keySelector: order => order.CustomerId,
    body: async (order, ct) =>
    {
        await ValidateInventory(order, ct);
        await ChargePayment(order, ct);
        await ShipOrder(order, ct);
    },
    maxConcurrency: 32,
    perKeyConcurrency: 1,
    enableFairScheduling: true);

foreach (var order in incomingOrders)
    await atom.EnqueueAsync(order);

// Customer A's orders: sequential
// Customer B's orders: sequential
// A and B: parallel

await atom.DrainAsync();
```

---

## Related Packages

| Package | Description |
|---------|-------------|
| [mostlylucid.ephemeral](https://www.nuget.org/packages/mostlylucid.ephemeral) | Core library |
| [mostlylucid.ephemeral.complete](https://www.nuget.org/packages/mostlylucid.ephemeral.complete) | All in one DLL |

## License

MIT
