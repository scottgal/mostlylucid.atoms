# Mostlylucid.Ephemeral.Atoms.KeyedSequential

Per-key sequential processing. Ensures ordering per key while allowing global parallelism.

## Installation

```bash
dotnet add package mostlylucid.ephemeral.atoms.keyedsequential
```

## Usage

```csharp
await using var atom = new KeyedSequentialAtom<Order, string>(
    keySelector: order => order.CustomerId,
    body: async (order, ct) => await ProcessOrder(order, ct),
    maxConcurrency: 8,
    perKeyConcurrency: 1);

await atom.EnqueueAsync(new Order { CustomerId = "A" });
await atom.EnqueueAsync(new Order { CustomerId = "B" });  // Runs in parallel
await atom.EnqueueAsync(new Order { CustomerId = "A" });  // Waits for first A
await atom.DrainAsync();
```

## Full Source (~50 lines)

```csharp
using Mostlylucid.Ephemeral;

namespace Mostlylucid.Ephemeral.Atoms.KeyedSequential;

public sealed class KeyedSequentialAtom<T, TKey> : IAsyncDisposable where TKey : notnull
{
    private readonly EphemeralKeyedWorkCoordinator<T, TKey> _coordinator;
    private long _id;

    public KeyedSequentialAtom(
        Func<T, TKey> keySelector,
        Func<T, CancellationToken, Task> body,
        int? maxConcurrency = null,
        int perKeyConcurrency = 1,
        bool enableFairScheduling = false,
        SignalSink? signals = null)
    {
        var options = new EphemeralOptions
        {
            MaxConcurrency = maxConcurrency is > 0 ? maxConcurrency.Value : Environment.ProcessorCount,
            MaxConcurrencyPerKey = Math.Max(1, perKeyConcurrency),
            EnableFairScheduling = enableFairScheduling,
            Signals = signals
        };

        _coordinator = new EphemeralKeyedWorkCoordinator<T, TKey>(keySelector, body, options);
    }

    public async ValueTask<long> EnqueueAsync(T item, CancellationToken ct = default)
    {
        await _coordinator.EnqueueAsync(item, ct).ConfigureAwait(false);
        return Interlocked.Increment(ref _id);
    }

    public async Task DrainAsync(CancellationToken ct = default)
    {
        _coordinator.Complete();
        await _coordinator.DrainAsync(ct).ConfigureAwait(false);
    }

    public IReadOnlyCollection<EphemeralOperationSnapshot> Snapshot() => _coordinator.GetSnapshot();

    public (int Pending, int Active, int Completed, int Failed) Stats()
        => (_coordinator.PendingCount, _coordinator.ActiveCount, _coordinator.TotalCompleted, _coordinator.TotalFailed);

    public ValueTask DisposeAsync() => _coordinator.DisposeAsync();
}
```

## License

MIT
