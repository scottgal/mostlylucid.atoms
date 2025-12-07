# Mostlylucid.Ephemeral.Patterns.ControlledFanOut

Global gate bounds total concurrency while per-key ordering is preserved.

## Installation

```bash
dotnet add package mostlylucid.ephemeral.patterns.controlledfanout
```

## Usage

```csharp
var fanout = new ControlledFanOut<string, Message>(
    msg => msg.UserId,
    async (msg, ct) => await ProcessAsync(msg, ct),
    maxGlobalConcurrency: 16,
    perKeyConcurrency: 1);

await fanout.EnqueueAsync(message);
await fanout.DrainAsync();
```

## Full Source (~40 lines)

```csharp
using Mostlylucid.Ephemeral;

namespace Mostlylucid.Ephemeral.Patterns.ControlledFanOut;

public sealed class ControlledFanOut<TKey, T> : IAsyncDisposable where TKey : notnull
{
    private readonly EphemeralKeyedWorkCoordinator<T, TKey> _coordinator;

    public ControlledFanOut(
        Func<T, TKey> keySelector,
        Func<T, CancellationToken, Task> body,
        int maxGlobalConcurrency,
        int perKeyConcurrency = 1,
        SignalSink? sink = null)
    {
        _coordinator = new EphemeralKeyedWorkCoordinator<T, TKey>(
            keySelector,
            body,
            new EphemeralOptions
            {
                MaxConcurrency = maxGlobalConcurrency,
                MaxConcurrencyPerKey = Math.Max(1, perKeyConcurrency),
                Signals = sink
            });
    }

    public ValueTask EnqueueAsync(T item, CancellationToken ct = default) =>
        _coordinator.EnqueueAsync(item, ct);

    public async Task DrainAsync(CancellationToken ct = default)
    {
        _coordinator.Complete();
        await _coordinator.DrainAsync(ct).ConfigureAwait(false);
    }

    public ValueTask DisposeAsync() => _coordinator.DisposeAsync();
}
```

## License

MIT
