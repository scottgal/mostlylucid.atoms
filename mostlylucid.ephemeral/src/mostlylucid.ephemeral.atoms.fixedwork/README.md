# Mostlylucid.Ephemeral.Atoms.FixedWork

Minimal atom for fixed-concurrency work pipelines. Simple API: enqueue, drain, snapshot.

## Installation

```bash
dotnet add package mostlylucid.ephemeral.atoms.fixedwork
```

## Usage

```csharp
await using var atom = new FixedWorkAtom<string>(
    async (item, ct) => await ProcessAsync(item, ct),
    maxConcurrency: 4);

await atom.EnqueueAsync("work-1");
await atom.EnqueueAsync("work-2");
await atom.DrainAsync();

var (pending, active, completed, failed) = atom.Stats();
```

## Full Source (~50 lines)

```csharp
using Mostlylucid.Ephemeral;

namespace Mostlylucid.Ephemeral.Atoms.FixedWork;

public sealed class FixedWorkAtom<T> : IAsyncDisposable
{
    private readonly EphemeralWorkCoordinator<T> _coordinator;

    public FixedWorkAtom(
        Func<T, CancellationToken, Task> body,
        int? maxConcurrency = null,
        int? maxTracked = null,
        SignalSink? signals = null)
    {
        var options = new EphemeralOptions
        {
            MaxConcurrency = maxConcurrency is > 0 ? maxConcurrency.Value : Environment.ProcessorCount,
            MaxTrackedOperations = maxTracked is > 0 ? maxTracked.Value : 200,
            Signals = signals
        };

        _coordinator = new EphemeralWorkCoordinator<T>(body, options);
    }

    public ValueTask<long> EnqueueAsync(T item, CancellationToken ct = default)
        => _coordinator.EnqueueWithIdAsync(item, ct);

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
