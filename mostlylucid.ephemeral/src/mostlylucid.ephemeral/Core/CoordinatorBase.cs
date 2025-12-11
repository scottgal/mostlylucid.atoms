using System.Collections.Concurrent;

namespace Mostlylucid.Ephemeral;

/// <summary>
///     Base class for ephemeral coordinators, consolidating common initialization and state management.
///     Eliminates ~50 lines of duplication per coordinator implementation.
/// </summary>
/// <param name="options">Configuration options for the coordinator.</param>
public abstract class CoordinatorBase(EphemeralOptions? options) : IAsyncDisposable, IOperationPinning, IOperationFinalization, IOperationEvictor
{
    protected readonly CancellationTokenSource _cts = new();
    protected readonly TaskCompletionSource _drainTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    protected readonly OperationEchoStore? _echoStore = (options ?? new EphemeralOptions()).EnableOperationEcho
        ? new OperationEchoStore((options ?? new EphemeralOptions()).OperationEchoRetention,
            (options ?? new EphemeralOptions()).OperationEchoCapacity)
        : null;
    protected readonly EphemeralOptions _options = options ?? new EphemeralOptions();
    protected readonly ManualResetEventSlim _pauseGate = new(true);
    protected readonly ConcurrentQueue<EphemeralOperation> _recent = new();
    protected readonly object _windowLock = new();

    protected int _activeTaskCount;
    protected bool _channelIterationComplete;
    protected bool _completed;
    protected long _lastReadCleanupTicks;
    protected long _lastTrimTicks;
    protected bool _paused;
    protected int _pendingCount;
    protected int _totalCompleted;
    protected int _totalEnqueued;
    protected int _totalFailed;

    /// <summary>
    ///     Gets current metrics snapshot with thread-safe volatile reads.
    /// </summary>
    public CoordinatorMetrics GetMetrics()
    {
        return new CoordinatorMetrics(
            ref _pendingCount,
            ref _activeTaskCount,
            ref _totalEnqueued,
            ref _totalCompleted,
            ref _totalFailed,
            ref _completed,
            ref _paused);
    }

    public int PendingCount => Volatile.Read(ref _pendingCount);
    public int ActiveCount => Volatile.Read(ref _activeTaskCount);
    public int TotalEnqueued => Volatile.Read(ref _totalEnqueued);
    public int TotalCompleted => Volatile.Read(ref _totalCompleted);
    public int TotalFailed => Volatile.Read(ref _totalFailed);
    public bool IsCompleted => Volatile.Read(ref _completed);
    public bool IsDrained => IsCompleted && PendingCount == 0 && ActiveCount == 0;
    public bool IsPaused => Volatile.Read(ref _paused);

    /// <summary>
    ///     Public access to coordinator options for atoms.
    /// </summary>
    public EphemeralOptions Options => _options;

    public event Action<EphemeralOperationSnapshot>? OperationFinalized;

    /// <summary>
    ///     Gets a snapshot of all operations in this coordinator's window.
    /// </summary>
    public abstract IReadOnlyCollection<EphemeralOperationSnapshot> GetSnapshot();

    public bool Pin(long operationId)
    {
        foreach (var op in _recent)
            if (op.Id == operationId)
            {
                op.IsPinned = true;
                return true;
            }
        return false;
    }

    public bool Unpin(long operationId)
    {
        foreach (var op in _recent)
            if (op.Id == operationId)
            {
                op.IsPinned = false;
                return true;
            }
        return false;
    }

    public bool Evict(long operationId)
    {
        lock (_windowLock)
        {
            var toKeep = new List<EphemeralOperation>();
            var found = false;
            while (_recent.TryDequeue(out var op))
                if (op.Id == operationId)
                    found = true;
                else
                    toKeep.Add(op);

            foreach (var op in toKeep)
                _recent.Enqueue(op);

            return found;
        }
    }

    public bool TryKill(long operationId)
    {
        if (operationId <= 0)
            return false;

        if (!TryRemoveOperation(operationId, out var candidate) || candidate is null)
            return false;

        if (candidate.Completed is null)
            candidate.Completed = DateTimeOffset.UtcNow;
        candidate.IsPinned = false;
        NotifyOperationFinalized(candidate);
        CleanupWindow();
        return true;
    }

    public void Pause()
    {
        Volatile.Write(ref _paused, true);
        _pauseGate.Reset();
    }

    public void Resume()
    {
        Volatile.Write(ref _paused, false);
        _pauseGate.Set();
    }

    public void Cancel()
    {
        Volatile.Write(ref _completed, true);
        _cts.Cancel();
    }

    public IReadOnlyList<OperationEcho> GetEchoes()
    {
        return _echoStore?.Snapshot() ?? Array.Empty<OperationEcho>();
    }

    /// <summary>
    ///     Gets all signals from recent operations.
    /// </summary>
    public IReadOnlyList<SignalEvent> GetSignals()
    {
        return GetSignalsCore(null);
    }

    /// <summary>
    ///     Gets signals from operations matching the predicate.
    /// </summary>
    public IReadOnlyList<SignalEvent> GetSignals(Func<EphemeralOperationSnapshot, bool>? predicate)
    {
        return GetSignalsCore(predicate);
    }

    /// <summary>
    ///     Gets signals from operations with a specific key.
    /// </summary>
    public IReadOnlyList<SignalEvent> GetSignalsByKey(string key)
    {
        var results = new List<SignalEvent>();
        foreach (var op in _recent)
        {
            if (op._signals is not { Count: > 0 })
                continue;
            if (op.Key != key)
                continue;
            var timestamp = op.Completed ?? op.Started;
            foreach (var signal in op._signals)
                results.Add(new SignalEvent(signal, op.Id, op.Key, timestamp));
        }
        return results;
    }

    /// <summary>
    ///     Gets signals matching a pattern (glob-style with * and ?).
    /// </summary>
    public IReadOnlyList<SignalEvent> GetSignalsByPattern(string pattern)
    {
        var results = new List<SignalEvent>();
        foreach (var op in _recent)
        {
            if (op._signals is not { Count: > 0 })
                continue;
            var timestamp = op.Completed ?? op.Started;
            foreach (var signal in op._signals)
                if (StringPatternMatcher.Matches(signal, pattern))
                    results.Add(new SignalEvent(signal, op.Id, op.Key, timestamp));
        }
        return results;
    }

    /// <summary>
    ///     Checks if any operation has emitted a specific signal.
    /// </summary>
    public bool HasSignal(string signalName)
    {
        foreach (var op in _recent)
        {
            if (op._signals is not { Count: > 0 })
                continue;
            var signals = op._signals;
            var count = signals.Count;
            for (var i = 0; i < count; i++)
                if (signals[i] == signalName)
                    return true;
        }
        return false;
    }

    /// <summary>
    ///     Checks if any operation has emitted a signal matching the pattern.
    /// </summary>
    public bool HasSignalMatching(string pattern)
    {
        foreach (var op in _recent)
        {
            if (op._signals is not { Count: > 0 })
                continue;
            var signals = op._signals;
            var count = signals.Count;
            for (var i = 0; i < count; i++)
                if (StringPatternMatcher.Matches(signals[i], pattern))
                    return true;
        }
        return false;
    }

    /// <summary>
    ///     Counts all signals across all operations.
    /// </summary>
    public int CountSignals()
    {
        var count = 0;
        foreach (var op in _recent)
            if (op._signals is { Count: > 0 } signals)
                count += signals.Count;
        return count;
    }

    protected IReadOnlyList<SignalEvent> GetSignalsCore(Func<EphemeralOperationSnapshot, bool>? predicate)
    {
        var results = new List<SignalEvent>();
        foreach (var op in _recent)
        {
            if (op._signals is not { Count: > 0 })
                continue;
            if (predicate != null && !predicate(op.ToSnapshot()))
                continue;
            var timestamp = op.Completed ?? op.Started;
            foreach (var signal in op._signals)
                results.Add(new SignalEvent(signal, op.Id, op.Key, timestamp));
        }
        return results;
    }

    protected void NotifyOperationFinalized(EphemeralOperation op)
    {
        // Clear this operation's signals from the sink - coordinator manages signal lifetime
        _options.Signals?.ClearOperation(op.Id);

        OperationFinalized?.Invoke(op.ToSnapshot());
        RecordEcho(op);
    }

    protected void RecordEcho(EphemeralOperation op)
    {
        if (_echoStore is null)
            return;

        var signals = op._signals?.ToArray();
        var echo = new OperationEcho(op.Id, op.Key, signals, DateTimeOffset.UtcNow);
        _echoStore.Add(echo);
    }

    protected bool TryRemoveOperation(long operationId, out EphemeralOperation? removed)
    {
        removed = null;
        var buffer = new List<EphemeralOperation>();
        var found = false;

        lock (_windowLock)
        {
            while (_recent.TryDequeue(out var op))
            {
                if (!found && op.Id == operationId)
                {
                    removed = op;
                    found = true;
                    continue;
                }
                buffer.Add(op);
            }

            foreach (var op in buffer)
                _recent.Enqueue(op);
        }

        return found;
    }

    protected void MaybeCleanupForRead()
    {
        var now = Environment.TickCount64;
        var lastCleanup = Volatile.Read(ref _lastReadCleanupTicks);
        if (now - lastCleanup < 500)
            return;

        if (Interlocked.CompareExchange(ref _lastReadCleanupTicks, now, lastCleanup) == lastCleanup)
            CleanupWindow();
    }

    protected void CleanupWindow()
    {
        lock (_windowLock)
        {
            TrimWindowSizeLocked();
            TrimWindowAgeLocked();
        }
    }

    protected void TrimWindowSizeLocked()
    {
        var max = _options.MaxTrackedOperations;
        if (max <= 0) return;

        var pinnedCount = 0;
        var scanBudget = _recent.Count;

        while (_recent.Count > max && scanBudget-- > 0)
        {
            if (!_recent.TryDequeue(out var candidate))
                break;

            if (candidate.IsPinned)
            {
                pinnedCount++;
                _recent.Enqueue(candidate);
                if (pinnedCount >= _recent.Count)
                    break;
            }
            else
            {
                NotifyOperationFinalized(candidate);
            }
        }
    }

    protected void TrimWindowAgeLocked()
    {
        if (_options.MaxOperationLifetime is not { } maxAge)
            return;

        var now = Environment.TickCount64;
        var lastTrim = Volatile.Read(ref _lastTrimTicks);
        if (now - lastTrim < 1000)
            return;
        Volatile.Write(ref _lastTrimTicks, now);

        var cutoff = DateTimeOffset.UtcNow - maxAge;
        var scanBudget = _recent.Count;

        while (scanBudget-- > 0 && _recent.TryDequeue(out var op))
            if (op.IsPinned || op.Started >= cutoff)
                _recent.Enqueue(op);
            else
                NotifyOperationFinalized(op);
    }

    protected void EnqueueOperation(EphemeralOperation op)
    {
        lock (_windowLock)
        {
            _recent.Enqueue(op);
        }
        CleanupWindow();
    }

    protected void SampleIfRequested()
    {
        var sampler = _options.OnSample;
        if (sampler is null) return;

        var snapshot = _recent.Select(x => x.ToSnapshot()).ToArray();
        if (snapshot.Length > 0)
            sampler(snapshot);
    }

    public virtual ValueTask DisposeAsync()
    {
        Dispose(true);
        return ValueTask.CompletedTask;
    }

    protected virtual void Dispose(bool disposing)
    {
        if (disposing)
        {
            _cts.Dispose();
            _pauseGate.Dispose();
        }
    }
}
