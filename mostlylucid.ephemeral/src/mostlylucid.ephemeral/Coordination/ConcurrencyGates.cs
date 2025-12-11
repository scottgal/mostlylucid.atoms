using System.Runtime.CompilerServices;

namespace Mostlylucid.Ephemeral;

public interface IConcurrencyGate : IAsyncDisposable
{
    ValueTask WaitAsync(CancellationToken cancellationToken);
    void Release();
    void UpdateLimit(int newLimit);
}

/// <summary>
///     Lightweight, dynamically adjustable async gate.
/// </summary>
public sealed class AdjustableConcurrencyGate : IConcurrencyGate
{
    private readonly object _lock = new();
    private readonly Queue<WaiterEntry> _waiters = new();
    private int _available;
    private bool _disposed;
    private int _limit;

    public AdjustableConcurrencyGate(int limit)
    {
        if (limit <= 0) throw new ArgumentOutOfRangeException(nameof(limit));
        _limit = limit;
        _available = limit;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ValueTask WaitAsync(CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            ThrowIfDisposed();
            if (cancellationToken.IsCancellationRequested) return ValueTask.FromCanceled(cancellationToken);

            if (_available > 0)
            {
                _available--;
                return ValueTask.CompletedTask;
            }

            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var entry = new WaiterEntry(tcs);

            if (cancellationToken.CanBeCanceled)
            {
                var registration = cancellationToken.Register(static state =>
                {
                    var e = (WaiterEntry)state!;
                    if (e.Tcs.TrySetCanceled()) e.Dispose();
                }, entry);

                entry.Attach(registration);
            }

            _waiters.Enqueue(entry);
            return new ValueTask(tcs.Task);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Release()
    {
        lock (_lock)
        {
            ThrowIfDisposed();

            while (_waiters.Count > 0)
            {
                var entry = _waiters.Dequeue();
                if (entry.Tcs.TrySetResult())
                {
                    entry.Dispose(); // Dispose the registration on success
                    return;
                }
                // If TrySetResult failed (already canceled), registration was disposed in callback
            }

            if (_available < _limit) _available++;
        }
    }

    public void UpdateLimit(int newLimit)
    {
        if (newLimit <= 0) throw new ArgumentOutOfRangeException(nameof(newLimit));

        lock (_lock)
        {
            ThrowIfDisposed();

            var oldLimit = _limit;
            _limit = newLimit;

            if (_available > _limit) _available = _limit;

            if (newLimit > oldLimit)
            {
                var delta = newLimit - oldLimit;
                for (var i = 0; i < delta; i++)
                    if (_waiters.Count > 0)
                    {
                        var entry = _waiters.Dequeue();
                        if (entry.Tcs.TrySetResult())
                            entry.Dispose();
                        else
                            i--; // Retry with next waiter
                    }
                    else
                    {
                        _available++;
                    }
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_lock)
        {
            if (_disposed) return ValueTask.CompletedTask;
            _disposed = true;
            while (_waiters.Count > 0)
            {
                var entry = _waiters.Dequeue();
                entry.Tcs.TrySetCanceled();
                entry.Dispose();
            }
        }

        return ValueTask.CompletedTask;
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(AdjustableConcurrencyGate));
    }

    private sealed class WaiterEntry
    {
        public WaiterEntry(TaskCompletionSource tcs)
        {
            Tcs = tcs;
        }

        public TaskCompletionSource Tcs { get; }
        public CancellationTokenRegistration Registration { get; private set; }

        public void Attach(CancellationTokenRegistration registration)
        {
            Registration = registration;
        }

        public void Dispose()
        {
            Registration.Dispose();
        }
    }
}

/// <summary>
///     Fixed-concurrency gate backed by SemaphoreSlim (hot-path fast).
/// </summary>
public sealed class FixedConcurrencyGate : IConcurrencyGate
{
    private readonly SemaphoreSlim _semaphore;
    private bool _disposed;

    public FixedConcurrencyGate(int limit)
    {
        if (limit <= 0) throw new ArgumentOutOfRangeException(nameof(limit));
        _semaphore = new SemaphoreSlim(limit, limit);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ValueTask WaitAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        return new ValueTask(_semaphore.WaitAsync(cancellationToken));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Release()
    {
        ThrowIfDisposed();
        _semaphore.Release();
    }

    public void UpdateLimit(int newLimit)
    {
        ThrowIfDisposed();
        throw new InvalidOperationException("Dynamic concurrency not enabled for this coordinator.");
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed) return ValueTask.CompletedTask;
        _disposed = true;
        _semaphore.Dispose();
        return ValueTask.CompletedTask;
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(FixedConcurrencyGate));
    }
}