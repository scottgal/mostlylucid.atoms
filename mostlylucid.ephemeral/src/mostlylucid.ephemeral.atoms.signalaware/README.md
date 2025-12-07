# Mostlylucid.Ephemeral.Atoms.SignalAware

Atom that pauses or cancels intake based on ambient signals. Perfect for circuit-breaker patterns.

## Installation

```bash
dotnet add package mostlylucid.ephemeral.atoms.signalaware
```

## Usage

```csharp
await using var atom = new SignalAwareAtom<Request>(
    async (req, ct) => await ProcessAsync(req, ct),
    cancelOn: new HashSet<string> { "circuit-open" },
    deferOn: new HashSet<string> { "backpressure" });

await atom.EnqueueAsync(new Request("data"));  // Works fine

atom.Raise("circuit-open");
var id = await atom.EnqueueAsync(new Request("more"));  // Returns -1 (rejected)

await atom.DrainAsync();
```

## Full Source (~70 lines)

```csharp
using Mostlylucid.Ephemeral;

namespace Mostlylucid.Ephemeral.Atoms.SignalAware;

public sealed class SignalAwareAtom<T> : IAsyncDisposable
{
    private readonly EphemeralWorkCoordinator<T> _coordinator;
    private readonly IReadOnlySet<string>? _cancelOn;
    private readonly HashSet<string> _ambient = new(StringComparer.Ordinal);

    public SignalAwareAtom(
        Func<T, CancellationToken, Task> body,
        IReadOnlySet<string>? cancelOn = null,
        IReadOnlySet<string>? deferOn = null,
        TimeSpan? deferInterval = null,
        int? maxDeferAttempts = null,
        SignalSink? signals = null,
        int? maxConcurrency = null)
    {
        var options = new EphemeralOptions
        {
            MaxConcurrency = maxConcurrency is > 0 ? maxConcurrency.Value : Environment.ProcessorCount,
            CancelOnSignals = cancelOn,
            DeferOnSignals = deferOn,
            DeferCheckInterval = deferInterval ?? TimeSpan.FromMilliseconds(100),
            MaxDeferAttempts = maxDeferAttempts ?? 50,
            Signals = signals
        };

        _coordinator = new EphemeralWorkCoordinator<T>(body, options);
        _cancelOn = cancelOn;
    }

    public ValueTask<long> EnqueueAsync(T item, CancellationToken ct = default)
    {
        if (_cancelOn is { Count: > 0 })
            foreach (var signal in _ambient)
                if (StringPatternMatcher.MatchesAny(signal, _cancelOn))
                    return ValueTask.FromResult(-1L);

        return _coordinator.EnqueueWithIdAsync(item, ct);
    }

    public void Raise(string signal)
    {
        if (!string.IsNullOrWhiteSpace(signal))
            _ambient.Add(signal);
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
