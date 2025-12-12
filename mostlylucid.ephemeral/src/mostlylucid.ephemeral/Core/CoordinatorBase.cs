using System.Collections.Concurrent;

namespace Mostlylucid.Ephemeral;

/// <summary>
///     Base class for ephemeral coordinators, consolidating common initialization and state management.
///     Eliminates ~50 lines of duplication per coordinator implementation.
/// </summary>
/// <typeparam name="TOperation">The operation type managed by this coordinator.</typeparam>
/// <param name="options">Configuration options for the coordinator.</param>
public abstract class CoordinatorBase<TOperation>(EphemeralOptions? options) : ICoordinator, IOperationPinning, IOperationFinalization, IOperationEvictor
    where TOperation : class, IEphemeralOperationCore
{
    protected readonly CancellationTokenSource _cts = new();
    protected readonly TaskCompletionSource _drainTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    protected readonly OperationEchoStore? _echoStore = (options ?? new EphemeralOptions()).EnableOperationEcho
        ? new OperationEchoStore((options ?? new EphemeralOptions()).OperationEchoRetention,
            (options ?? new EphemeralOptions()).OperationEchoCapacity)
        : null;
    protected readonly EphemeralOptions _options = InitializeOptions(options, null!);
    protected readonly ManualResetEventSlim _pauseGate = new(true);
    protected readonly ConcurrentQueue<TOperation> _recent = new();
    protected readonly object _windowLock = new();

    // Coordinator-level signals (separate from operation signals)
    private readonly List<SignalEvent> _coordinatorSignals = new();
    private readonly object _coordinatorSignalLock = new();
    private readonly long _coordinatorId = EphemeralIdGenerator.NextId();

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

    // ISignalSource/ISignalEmitter implementation
    long ISignalEmitter.OperationId => _coordinatorId;
    string? ISignalEmitter.Key => null;

    /// <summary>
    ///     Unique ID for this coordinator.
    /// </summary>
    public long Id => _coordinatorId;

    // Initialize options and register with sink
    private static EphemeralOptions InitializeOptions(EphemeralOptions? options, ICoordinator coordinator)
    {
        var opts = options ?? new EphemeralOptions();
        opts.Signals?.RegisterCoordinator(coordinator);
        return opts;
    }

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
            var toKeep = new List<TOperation>();
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
        Signal("coordinator.paused");
    }

    public void Resume()
    {
        Volatile.Write(ref _paused, false);
        _pauseGate.Set();
        Signal("coordinator.resumed");
    }

    public void Cancel()
    {
        Volatile.Write(ref _completed, true);
        _cts.Cancel();
        Signal("coordinator.cancelled");
    }

    // ISignalSource signal emission methods
    /// <summary>
    ///     Emits a coordinator-level signal.
    /// </summary>
    public void Signal(string signal)
    {
        var evt = new SignalEvent(signal, _coordinatorId, null, DateTimeOffset.UtcNow, null);
        lock (_coordinatorSignalLock)
        {
            _coordinatorSignals.Add(evt);
        }
        _options.Signals?.Raise(evt);
    }

    /// <summary>
    ///     Emits a coordinator-level signal (alias for Signal).
    /// </summary>
    void ISignalEmitter.Emit(string signal) => Signal(signal);

    /// <summary>
    ///     Emits a coordinator-level signal with propagation tracking.
    /// </summary>
    bool ISignalEmitter.EmitCaused(string signal, SignalPropagation? cause)
    {
        var evt = new SignalEvent(signal, _coordinatorId, null, DateTimeOffset.UtcNow, cause);
        lock (_coordinatorSignalLock)
        {
            _coordinatorSignals.Add(evt);
        }
        _options.Signals?.Raise(evt);
        return true;
    }

    /// <summary>
    ///     Retracts a coordinator-level signal.
    /// </summary>
    bool ISignalEmitter.Retract(string signal)
    {
        lock (_coordinatorSignalLock)
        {
            for (int i = _coordinatorSignals.Count - 1; i >= 0; i--)
            {
                if (_coordinatorSignals[i].Signal == signal)
                {
                    _coordinatorSignals.RemoveAt(i);
                    return true;
                }
            }
        }
        return false;
    }

    /// <summary>
    ///     Retracts all coordinator-level signals matching a pattern.
    /// </summary>
    int ISignalEmitter.RetractMatching(string pattern)
    {
        lock (_coordinatorSignalLock)
        {
            return _coordinatorSignals.RemoveAll(evt => StringPatternMatcher.Matches(evt.Signal, pattern));
        }
    }

    /// <summary>
    ///     Checks if the coordinator has a specific signal.
    /// </summary>
    bool ISignalEmitter.HasSignal(string signal)
    {
        lock (_coordinatorSignalLock)
        {
            return _coordinatorSignals.Any(evt => evt.Signal == signal);
        }
    }

    public IReadOnlyList<OperationEcho> GetEchoes()
    {
        return _echoStore?.Snapshot() ?? Array.Empty<OperationEcho>();
    }

    /// <summary>
    ///     Gets all signals - includes both coordinator-level signals and operation signals.
    /// </summary>
    public IReadOnlyList<SignalEvent> GetSignals()
    {
        var results = new List<SignalEvent>();

        // Add coordinator-level signals
        lock (_coordinatorSignalLock)
        {
            results.AddRange(_coordinatorSignals);
        }

        // Add operation signals
        results.AddRange(GetSignalsCore(null));

        return results;
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
            var signals = op.GetSignals();
            if (signals.Count == 0)
                continue;
            if (op.Key != key)
                continue;
            results.AddRange(signals);
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
            var signals = op.GetSignals();
            if (signals.Count == 0)
                continue;
            foreach (var evt in signals)
                if (StringPatternMatcher.Matches(evt.Signal, pattern))
                    results.Add(evt);
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
            var signals = op.GetSignals();
            if (signals.Count == 0)
                continue;
            var count = signals.Count;
            for (var i = 0; i < count; i++)
                if (signals[i].Signal == signalName)
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
            var signals = op.GetSignals();
            if (signals.Count == 0)
                continue;
            var count = signals.Count;
            for (var i = 0; i < count; i++)
                if (StringPatternMatcher.Matches(signals[i].Signal, pattern))
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
        {
            var signals = op.GetSignals();
            if (signals.Count > 0)
                count += signals.Count;
        }
        return count;
    }

    /// <summary>
    ///     Cleans up signals older than the specified age - includes both coordinator and operation signals.
    /// </summary>
    public int CleanupSignals(TimeSpan olderThan)
    {
        var removed = 0;

        // Cleanup coordinator signals
        var cutoff = DateTimeOffset.UtcNow - olderThan;
        lock (_coordinatorSignalLock)
        {
            removed += _coordinatorSignals.RemoveAll(s => s.Timestamp < cutoff);
        }

        // Cleanup operation signals
        foreach (var op in _recent)
            removed += op.CleanupSignals(olderThan);

        return removed;
    }

    /// <summary>
    ///     Cleans up the oldest N signals from the coordinator, keeping only the most recent N.
    ///     Also asks each operation to clean up its oldest signals.
    /// </summary>
    public int CleanupSignals(int keepCount)
    {
        var removed = 0;

        // Cleanup coordinator signals
        lock (_coordinatorSignalLock)
        {
            if (_coordinatorSignals.Count > keepCount)
            {
                var toRemove = _coordinatorSignals.Count - keepCount;
                _coordinatorSignals.RemoveRange(0, toRemove);
                removed += toRemove;
            }
        }

        // Cleanup operation signals
        foreach (var op in _recent)
            removed += op.CleanupSignals(keepCount);

        return removed;
    }

    /// <summary>
    ///     Cleans up signals matching the pattern - includes both coordinator and operation signals.
    /// </summary>
    public int CleanupSignals(string pattern)
    {
        var removed = 0;

        // Cleanup coordinator signals
        lock (_coordinatorSignalLock)
        {
            removed += _coordinatorSignals.RemoveAll(evt => StringPatternMatcher.Matches(evt.Signal, pattern));
        }

        // Cleanup operation signals
        foreach (var op in _recent)
            removed += op.CleanupSignals(pattern);

        return removed;
    }

    protected IReadOnlyList<SignalEvent> GetSignalsCore(Func<EphemeralOperationSnapshot, bool>? predicate)
    {
        var results = new List<SignalEvent>();
        foreach (var op in _recent)
        {
            var signals = op.GetSignals();
            if (signals.Count == 0)
                continue;
            if (predicate != null && !predicate(op.ToSnapshot()))
                continue;
            results.AddRange(signals);
        }
        return results;
    }

    protected void NotifyOperationFinalized(TOperation op)
    {
        // Operations own their signals - no manual cleanup needed
        OperationFinalized?.Invoke(op.ToSnapshot());
        RecordEcho(op);
    }

    protected void RecordEcho(TOperation op)
    {
        if (_echoStore is null)
            return;

        var signals = op.GetSignals().ToArray();
        var echo = new OperationEcho(op.Id, op.Key, signals, DateTimeOffset.UtcNow);
        _echoStore.Add(echo);
    }

    protected bool TryRemoveOperation(long operationId, out TOperation? removed)
    {
        removed = null;
        var buffer = new List<TOperation>();
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

    protected void EnqueueOperation(TOperation op)
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
