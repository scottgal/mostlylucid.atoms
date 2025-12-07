# Mostlylucid.Ephemeral.Patterns.Backpressure

Signal-driven backpressure - defer intake when backpressure signals present.

## Installation

```bash
dotnet add package mostlylucid.ephemeral.patterns.backpressure
```

## Usage

```csharp
var sink = new SignalSink();
var coordinator = SignalDrivenBackpressure.Create<WorkItem>(
    async (item, ct) => await ProcessAsync(item, ct),
    sink,
    maxConcurrency: 4);

sink.Raise("backpressure.downstream");  // New work defers
await coordinator.EnqueueAsync(item);
```

## Full Source (~30 lines)

```csharp
using Mostlylucid.Ephemeral;

namespace Mostlylucid.Ephemeral.Patterns.Backpressure;

public static class SignalDrivenBackpressure
{
    public static EphemeralWorkCoordinator<T> Create<T>(
        Func<T, CancellationToken, Task> body,
        SignalSink sink,
        int maxConcurrency = 4)
    {
        return new EphemeralWorkCoordinator<T>(
            body,
            new EphemeralOptions
            {
                MaxConcurrency = maxConcurrency,
                Signals = sink,
                DeferOnSignals = new HashSet<string> { "backpressure.*" },
                DeferCheckInterval = TimeSpan.FromMilliseconds(50),
                MaxDeferAttempts = 200
            });
    }
}
```

## License

MIT
