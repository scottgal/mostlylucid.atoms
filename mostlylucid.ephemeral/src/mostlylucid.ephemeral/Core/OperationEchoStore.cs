using System.Collections.Concurrent;

namespace Mostlylucid.Ephemeral;

internal sealed class OperationEchoStore
{
    private readonly int _capacity;
    private readonly object _lock = new();
    private readonly ConcurrentQueue<OperationEcho> _queue = new();
    private readonly TimeSpan _retention;

    public OperationEchoStore(TimeSpan retention, int capacity)
    {
        _retention = retention > TimeSpan.Zero ? retention : TimeSpan.FromMinutes(1);
        _capacity = Math.Max(1, capacity);
    }

    public void Add(OperationEcho echo)
    {
        if (echo is null) return;

        lock (_lock)
        {
            _queue.Enqueue(echo);
            TrimLocked();
        }
    }

    public IReadOnlyList<OperationEcho> Snapshot()
    {
        lock (_lock)
        {
            TrimLocked();
            return _queue.ToArray();
        }
    }

    private void TrimLocked()
    {
        var cutoff = DateTimeOffset.UtcNow - _retention;

        while (_queue.Count > 0)
        {
            if (_queue.TryPeek(out var head))
                if (head.FinalizedAt < cutoff || _queue.Count > _capacity)
                {
                    _queue.TryDequeue(out _);
                    continue;
                }

            break;
        }
    }
}