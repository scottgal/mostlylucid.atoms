# Mostlylucid.Ephemeral.Atoms.Retry

Wraps work with bounded retry and exponential backoff semantics.

## Installation

```bash
dotnet add package mostlylucid.ephemeral.atoms.retry
```

## Usage

```csharp
await using var atom = new RetryAtom<HttpRequest>(
    async (req, ct) => await SendAsync(req, ct),
    maxAttempts: 3,
    backoff: attempt => TimeSpan.FromMilliseconds(100 * Math.Pow(2, attempt)));

await atom.EnqueueAsync(new HttpRequest("https://api.example.com"));
await atom.DrainAsync();
```

## Full Source (~60 lines)

```csharp
using Mostlylucid.Ephemeral;

namespace Mostlylucid.Ephemeral.Atoms.Retry;

public sealed class RetryAtom<T> : IAsyncDisposable
{
    private readonly EphemeralWorkCoordinator<T> _coordinator;
    private readonly int _maxAttempts;
    private readonly Func<int, TimeSpan> _backoff;

    public RetryAtom(
        Func<T, CancellationToken, Task> body,
        int maxAttempts = 3,
        Func<int, TimeSpan>? backoff = null,
        int? maxConcurrency = null,
        SignalSink? signals = null)
    {
        if (maxAttempts <= 0) throw new ArgumentOutOfRangeException(nameof(maxAttempts));
        _maxAttempts = maxAttempts;
        _backoff = backoff ?? (attempt => TimeSpan.FromMilliseconds(50 * attempt));

        _coordinator = new EphemeralWorkCoordinator<T>(
            async (item, ct) =>
            {
                var attempt = 0;
                while (true)
                {
                    try
                    {
                        await body(item, ct).ConfigureAwait(false);
                        return;
                    }
                    catch when (++attempt < _maxAttempts && !ct.IsCancellationRequested)
                    {
                        await Task.Delay(_backoff(attempt), ct).ConfigureAwait(false);
                    }
                }
            },
            new EphemeralOptions
            {
                MaxConcurrency = maxConcurrency is > 0 ? maxConcurrency.Value : Environment.ProcessorCount,
                Signals = signals
            });
    }

    public ValueTask<long> EnqueueAsync(T item, CancellationToken ct = default)
        => _coordinator.EnqueueWithIdAsync(item, ct);

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
