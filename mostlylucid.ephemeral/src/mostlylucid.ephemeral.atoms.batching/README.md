# Mostlylucid.Ephemeral.Atoms.Batching

Collects items into batches. Processes when full or when time interval elapses.

## Installation

```bash
dotnet add package mostlylucid.ephemeral.atoms.batching
```

## Usage

```csharp
await using var atom = new BatchingAtom<LogEntry>(
    async (batch, ct) => await WriteBatchAsync(batch, ct),
    maxBatchSize: 100,
    flushInterval: TimeSpan.FromSeconds(5));

atom.Enqueue(new LogEntry("Event 1"));
atom.Enqueue(new LogEntry("Event 2"));
// Batch processes when 100 items OR 5 seconds elapsed
```

## Full Source (~90 lines)

```csharp
using System.Timers;
using Timer = System.Timers.Timer;

namespace Mostlylucid.Ephemeral.Atoms.Batching;

public sealed class BatchingAtom<T> : IAsyncDisposable
{
    private readonly object _lock = new();
    private readonly List<T> _buffer = new();
    private readonly Func<IReadOnlyList<T>, CancellationToken, Task> _onBatch;
    private readonly int _maxBatchSize;
    private readonly Timer _timer;
    private bool _flushing;
    private bool _disposed;

    public BatchingAtom(
        Func<IReadOnlyList<T>, CancellationToken, Task> onBatch,
        int maxBatchSize = 32,
        TimeSpan? flushInterval = null)
    {
        if (maxBatchSize <= 0) throw new ArgumentOutOfRangeException(nameof(maxBatchSize));
        _onBatch = onBatch ?? throw new ArgumentNullException(nameof(onBatch));
        _maxBatchSize = maxBatchSize;
        _timer = new Timer((flushInterval ?? TimeSpan.FromSeconds(1)).TotalMilliseconds)
        {
            AutoReset = true,
            Enabled = true
        };
        _timer.Elapsed += async (_, _) => await TryFlushAsync().ConfigureAwait(false);
    }

    public void Enqueue(T item)
    {
        lock (_lock)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(BatchingAtom<T>));
            _buffer.Add(item);
            if (_buffer.Count >= _maxBatchSize && !_flushing)
                _ = FlushAsync();
        }
    }

    private async Task TryFlushAsync()
    {
        lock (_lock)
        {
            if (_flushing || _buffer.Count == 0) return;
            _flushing = true;
        }
        try { await FlushAsync().ConfigureAwait(false); }
        finally { lock (_lock) { _flushing = false; } }
    }

    private async Task FlushAsync()
    {
        List<T> batch;
        lock (_lock)
        {
            if (_buffer.Count == 0) return;
            batch = new List<T>(_buffer);
            _buffer.Clear();
        }
        await _onBatch(batch, CancellationToken.None).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Stop();
        _timer.Dispose();
        await FlushAsync().ConfigureAwait(false);
    }
}
```

## License

MIT
