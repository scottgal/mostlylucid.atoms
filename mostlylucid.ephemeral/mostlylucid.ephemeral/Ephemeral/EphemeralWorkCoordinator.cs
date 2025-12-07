using System.Collections.Concurrent;
using System.Threading.Channels;

namespace Mostlylucid.Helpers.Ephemeral;

/// <summary>
/// A long-lived, observable work coordinator that accepts items continuously.
/// Unlike EphemeralForEachAsync (which processes a collection), this stays alive
/// and lets you enqueue items over time, inspect operations, and gracefully shutdown.
/// </summary>
public sealed class EphemeralWorkCoordinator<T> : IAsyncDisposable
{
    private readonly record struct WorkItem(T Item, long? Id);

    private readonly Channel<WorkItem> _channel;
    private readonly Func<T, CancellationToken, Task> _body;
    private readonly EphemeralOptions _options;
    private readonly CancellationTokenSource _cts;
    private readonly ConcurrentQueue<EphemeralOperation> _recent;
    private readonly IConcurrencyGate _concurrency;
    private readonly ManualResetEventSlim _pauseGate = new(initialState: true); // true = not paused
    private readonly object _windowLock = new(); // Protects _recent during Evict/Trim operations
    private readonly Task _processingTask;
    private readonly Task? _sourceConsumerTask;
    private readonly TaskCompletionSource _drainTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _completed;
    private bool _channelIterationComplete;
    private bool _paused;
    private int _pendingCount;
    private int _totalEnqueued;
    private int _totalCompleted;
    private int _totalFailed;
    private int _activeTaskCount;
    private int _currentMaxConcurrency;
    private long _lastTrimTicks; // For throttling TrimWindowAge
    private long _lastReadCleanupTicks; // For throttling cleanup on read operations

    /// <summary>
    /// Creates a coordinator that accepts manual enqueues via EnqueueAsync/TryEnqueue.
    /// </summary>
    public EphemeralWorkCoordinator(
        Func<T, CancellationToken, Task> body,
        EphemeralOptions? options = null)
    {
        _body = body ?? throw new ArgumentNullException(nameof(body));
        _options = options ?? new EphemeralOptions();
        _cts = new CancellationTokenSource();
        _recent = new ConcurrentQueue<EphemeralOperation>();
        _concurrency = CreateGate(_options);
        _currentMaxConcurrency = _options.MaxConcurrency;

        // Bounded channel provides back-pressure
        _channel = Channel.CreateBounded<WorkItem>(new BoundedChannelOptions(_options.MaxTrackedOperations)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = false,
            SingleWriter = false
        });

        _processingTask = ProcessAsync();
    }

    /// <summary>
    /// Creates a coordinator that continuously consumes from an IAsyncEnumerable source.
    /// Runs until the source completes or cancellation is requested.
    /// </summary>
    private EphemeralWorkCoordinator(
        IAsyncEnumerable<T> source,
        Func<T, CancellationToken, Task> body,
        EphemeralOptions? options)
    {
        _body = body ?? throw new ArgumentNullException(nameof(body));
        _options = options ?? new EphemeralOptions();
        _cts = new CancellationTokenSource();
        _recent = new ConcurrentQueue<EphemeralOperation>();
        _concurrency = CreateGate(_options);
        _currentMaxConcurrency = _options.MaxConcurrency;

        _channel = Channel.CreateBounded<WorkItem>(new BoundedChannelOptions(_options.MaxTrackedOperations)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = false,
            SingleWriter = false
        });

        _processingTask = ProcessAsync();
        _sourceConsumerTask = ConsumeSourceAsync(source);
    }

    /// <summary>
    /// Creates a coordinator that continuously consumes from an IAsyncEnumerable source.
    /// Runs until the source completes or cancellation is requested.
    /// </summary>
    public static EphemeralWorkCoordinator<T> FromAsyncEnumerable(
        IAsyncEnumerable<T> source,
        Func<T, CancellationToken, Task> body,
        EphemeralOptions? options = null)
    {
        return new EphemeralWorkCoordinator<T>(source, body, options);
    }

    /// <summary>
    /// Number of items waiting to be processed.
    /// </summary>
    public int PendingCount => Volatile.Read(ref _pendingCount);

    /// <summary>
    /// Number of items currently being processed.
    /// </summary>
    public int ActiveCount => Volatile.Read(ref _activeTaskCount);

    /// <summary>
    /// Total items enqueued since creation.
    /// </summary>
    public int TotalEnqueued => Volatile.Read(ref _totalEnqueued);

    /// <summary>
    /// Total items completed successfully.
    /// </summary>
    public int TotalCompleted => Volatile.Read(ref _totalCompleted);

    /// <summary>
    /// Total items that failed with an exception.
    /// </summary>
    public int TotalFailed => Volatile.Read(ref _totalFailed);

    /// <summary>
    /// Whether Complete() has been called.
    /// </summary>
    public bool IsCompleted => Volatile.Read(ref _completed);

    /// <summary>
    /// Whether all work is done (completed + drained).
    /// </summary>
    public bool IsDrained => IsCompleted && PendingCount == 0 && ActiveCount == 0;

    /// <summary>
    /// Whether the coordinator is paused.
    /// When paused, no new items are pulled from the queue (but running operations continue).
    /// </summary>
    public bool IsPaused => Volatile.Read(ref _paused);

    /// <summary>
    /// Current max concurrency (tracks dynamic changes when enabled).
    /// </summary>
    public int CurrentMaxConcurrency => Volatile.Read(ref _currentMaxConcurrency);

    /// <summary>
    /// Pause processing. Running operations continue, but no new items are started.
    /// </summary>
    public void Pause()
    {
        Volatile.Write(ref _paused, true);
        _pauseGate.Reset();
    }

    /// <summary>
    /// Resume processing after a pause.
    /// </summary>
    public void Resume()
    {
        Volatile.Write(ref _paused, false);
        _pauseGate.Set();
    }

    /// <summary>
    /// Gets a snapshot of recent operations (both running and completed).
    /// </summary>
    public IReadOnlyCollection<EphemeralOperationSnapshot> GetSnapshot()
    {
        MaybeCleanupForRead();
        return _recent.Select(x => x.ToSnapshot()).ToArray();
    }

    /// <summary>
    /// Gets only the currently running operations.
    /// </summary>
    public IReadOnlyCollection<EphemeralOperationSnapshot> GetRunning()
    {
        MaybeCleanupForRead();
        return _recent
            .Where(x => x.Completed is null)
            .Select(x => x.ToSnapshot())
            .ToArray();
    }

    /// <summary>
    /// Gets only the completed operations (success or failure).
    /// </summary>
    public IReadOnlyCollection<EphemeralOperationSnapshot> GetCompleted()
    {
        MaybeCleanupForRead();
        return _recent
            .Where(x => x.Completed is not null)
            .Select(x => x.ToSnapshot())
            .ToArray();
    }

    /// <summary>
    /// Gets only the failed operations.
    /// </summary>
    public IReadOnlyCollection<EphemeralOperationSnapshot> GetFailed()
    {
        MaybeCleanupForRead();
        return _recent
            .Where(x => x.Error is not null)
            .Select(x => x.ToSnapshot())
            .ToArray();
    }

    /// <summary>
    /// Throttled cleanup for read operations - only runs every 500ms to reduce lock contention.
    /// Write operations (enqueue, completion) still trigger immediate cleanup.
    /// </summary>
    private void MaybeCleanupForRead()
    {
        var now = Environment.TickCount64;
        var lastCleanup = Volatile.Read(ref _lastReadCleanupTicks);
        if (now - lastCleanup < 500)
            return; // Skip if we cleaned up recently

        if (Interlocked.CompareExchange(ref _lastReadCleanupTicks, now, lastCleanup) == lastCleanup)
        {
            CleanupWindow();
        }
    }

    /// <summary>
    /// Gets all signals from recent operations with their source operation identity.
    /// </summary>
    public IReadOnlyList<SignalEvent> GetSignals() => GetSignalsCore(null);

    /// <summary>
    /// Gets signals from operations matching the predicate.
    /// Use to limit scanning to specific operations (e.g., by key or time range).
    /// Note: Creates a snapshot for each operation with signals - use specialized overloads for better performance.
    /// </summary>
    public IReadOnlyList<SignalEvent> GetSignals(Func<EphemeralOperationSnapshot, bool>? predicate)
        => GetSignalsCore(predicate);

    /// <summary>
    /// Gets signals from operations with a specific key.
    /// Zero-allocation filtering - no snapshot created.
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
            {
                results.Add(new SignalEvent(signal, op.Id, op.Key, timestamp));
            }
        }
        return results;
    }

    /// <summary>
    /// Gets signals from operations started within a time range.
    /// Zero-allocation filtering - no snapshot created.
    /// </summary>
    public IReadOnlyList<SignalEvent> GetSignalsByTimeRange(DateTimeOffset from, DateTimeOffset to)
    {
        var results = new List<SignalEvent>();
        foreach (var op in _recent)
        {
            if (op._signals is not { Count: > 0 })
                continue;

            if (op.Started < from || op.Started > to)
                continue;

            var timestamp = op.Completed ?? op.Started;
            foreach (var signal in op._signals)
            {
                results.Add(new SignalEvent(signal, op.Id, op.Key, timestamp));
            }
        }
        return results;
    }

    /// <summary>
    /// Gets signals from operations started after a specific time.
    /// Zero-allocation filtering - no snapshot created.
    /// </summary>
    public IReadOnlyList<SignalEvent> GetSignalsSince(DateTimeOffset since)
    {
        var results = new List<SignalEvent>();
        foreach (var op in _recent)
        {
            if (op._signals is not { Count: > 0 })
                continue;

            if (op.Started < since)
                continue;

            var timestamp = op.Completed ?? op.Started;
            foreach (var signal in op._signals)
            {
                results.Add(new SignalEvent(signal, op.Id, op.Key, timestamp));
            }
        }
        return results;
    }

    /// <summary>
    /// Gets signals matching a specific signal name from all operations.
    /// Zero-allocation filtering - no snapshot created.
    /// </summary>
    public IReadOnlyList<SignalEvent> GetSignalsByName(string signalName)
    {
        var results = new List<SignalEvent>();
        foreach (var op in _recent)
        {
            if (op._signals is not { Count: > 0 })
                continue;

            var timestamp = op.Completed ?? op.Started;
            foreach (var signal in op._signals)
            {
                if (signal == signalName)
                {
                    results.Add(new SignalEvent(signal, op.Id, op.Key, timestamp));
                }
            }
        }
        return results;
    }

    /// <summary>
    /// Gets signals matching a pattern (glob-style with * and ?) from all operations.
    /// Zero-allocation filtering - no snapshot created.
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
            {
                if (StringPatternMatcher.Matches(signal, pattern))
                {
                    results.Add(new SignalEvent(signal, op.Id, op.Key, timestamp));
                }
            }
        }
        return results;
    }

    /// <summary>
    /// Checks if any operation has emitted a specific signal.
    /// Short-circuits on first match for O(1) best case.
    /// </summary>
    public bool HasSignal(string signalName)
    {
        foreach (var op in _recent)
        {
            if (op._signals is not { Count: > 0 })
                continue;

            foreach (var signal in op._signals)
            {
                if (signal == signalName)
                    return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Checks if any operation has emitted a signal matching the pattern.
    /// Short-circuits on first match for O(1) best case.
    /// </summary>
    public bool HasSignalMatching(string pattern)
    {
        foreach (var op in _recent)
        {
            if (op._signals is not { Count: > 0 })
                continue;

            foreach (var signal in op._signals)
            {
                if (StringPatternMatcher.Matches(signal, pattern))
                    return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Counts all signals across all operations.
    /// More efficient than GetSignals().Count as it doesn't allocate SignalEvent structs.
    /// </summary>
    public int CountSignals()
    {
        var count = 0;
        foreach (var op in _recent)
        {
            if (op._signals is { Count: > 0 } signals)
                count += signals.Count;
        }
        return count;
    }

    /// <summary>
    /// Counts signals matching a specific name.
    /// More efficient than GetSignalsByName().Count.
    /// </summary>
    public int CountSignals(string signalName)
    {
        var count = 0;
        foreach (var op in _recent)
        {
            if (op._signals is not { Count: > 0 })
                continue;

            foreach (var signal in op._signals)
            {
                if (signal == signalName)
                    count++;
            }
        }
        return count;
    }

    /// <summary>
    /// Counts signals matching a pattern.
    /// More efficient than GetSignalsByPattern().Count.
    /// </summary>
    public int CountSignalsMatching(string pattern)
    {
        var count = 0;
        foreach (var op in _recent)
        {
            if (op._signals is not { Count: > 0 })
                continue;

            foreach (var signal in op._signals)
            {
                if (StringPatternMatcher.Matches(signal, pattern))
                    count++;
            }
        }
        return count;
    }

    private IReadOnlyList<SignalEvent> GetSignalsCore(Func<EphemeralOperationSnapshot, bool>? predicate)
    {
        var results = new List<SignalEvent>();
        foreach (var op in _recent)
        {
            // Access fields directly to avoid snapshot allocation when filtering
            if (op._signals is not { Count: > 0 })
                continue;

            // Only create snapshot if we need to filter
            if (predicate != null && !predicate(op.ToSnapshot()))
                continue;

            var timestamp = op.Completed ?? op.Started;
            foreach (var signal in op._signals)
            {
                results.Add(new SignalEvent(signal, op.Id, op.Key, timestamp));
            }
        }
        return results;
    }

    /// <summary>
    /// Pin an operation by ID so it survives eviction.
    /// Returns true if found and pinned.
    /// In single-concurrency pipelines, prefer offloading long-lived/pinned tasks to a sub-coordinator to avoid shrinking the active window.
    /// </summary>
    public bool Pin(long operationId)
    {
        foreach (var op in _recent)
        {
            if (op.Id == operationId)
            {
                op.IsPinned = true;
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Unpin an operation by ID, allowing it to be evicted normally.
    /// Returns true if found and unpinned.
    /// </summary>
    public bool Unpin(long operationId)
    {
        foreach (var op in _recent)
        {
            if (op.Id == operationId)
            {
                op.IsPinned = false;
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Forcibly remove an operation from the window by ID.
    /// Removes even if pinned. Returns true if found and removed.
    /// Thread-safe but briefly blocks other window operations.
    /// </summary>
    public bool Evict(long operationId)
    {
        lock (_windowLock)
        {
            // ConcurrentQueue doesn't support removal, so we rebuild without the target
            var toKeep = new List<EphemeralOperation>();
            var found = false;
            while (_recent.TryDequeue(out var op))
            {
                if (op.Id == operationId)
                {
                    found = true;
                    // Don't re-add this one
                }
                else
                {
                    toKeep.Add(op);
                }
            }
            foreach (var op in toKeep)
            {
                _recent.Enqueue(op);
            }
            return found;
        }
    }

    /// <summary>
    /// Enqueue a new item for processing. Blocks if at capacity.
    /// </summary>
    public async ValueTask EnqueueAsync(T item, CancellationToken cancellationToken = default)
    {
        if (_completed)
            throw new InvalidOperationException("Coordinator has been completed; no new items accepted.");

        await _channel.Writer.WriteAsync(new WorkItem(item, null), cancellationToken).ConfigureAwait(false);
        Interlocked.Increment(ref _pendingCount);
        Interlocked.Increment(ref _totalEnqueued);
    }

    /// <summary>
    /// Enqueue a new item for processing. Blocks if at capacity.
    /// Returns the operation ID for tracking.
    /// </summary>
    public async ValueTask<long> EnqueueWithIdAsync(T item, CancellationToken cancellationToken = default)
    {
        if (_completed)
            throw new InvalidOperationException("Coordinator has been completed; no new items accepted.");

        var id = EphemeralIdGenerator.NextId();

        await _channel.Writer.WriteAsync(new WorkItem(item, id), cancellationToken).ConfigureAwait(false);
        Interlocked.Increment(ref _pendingCount);
        Interlocked.Increment(ref _totalEnqueued);

        return id;
    }

    /// <summary>
    /// Try to enqueue without blocking. Returns false if at capacity.
    /// </summary>
    public bool TryEnqueue(T item)
    {
        if (_completed)
            return false;

        if (_channel.Writer.TryWrite(new WorkItem(item, null)))
        {
            Interlocked.Increment(ref _pendingCount);
            Interlocked.Increment(ref _totalEnqueued);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Signal that no more items will be added. Processing continues until drained.
    /// </summary>
    public void Complete()
    {
        Volatile.Write(ref _completed, true);
        _channel.Writer.Complete();
    }

    /// <summary>
    /// Wait for all enqueued work to complete.
    /// For manual enqueue mode, call Complete() first.
    /// For IAsyncEnumerable mode, waits for source to complete.
    /// </summary>
    public async Task DrainAsync(CancellationToken cancellationToken = default)
    {
        // For IAsyncEnumerable source, wait for it to complete first
        if (_sourceConsumerTask is not null)
        {
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _cts.Token);
            await _sourceConsumerTask.WaitAsync(linkedCts.Token).ConfigureAwait(false);
        }
        else if (!_completed)
        {
            throw new InvalidOperationException("Call Complete() before DrainAsync().");
        }

        using var linkedCts2 = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _cts.Token);
        await _processingTask.WaitAsync(linkedCts2.Token).ConfigureAwait(false);
    }

    /// <summary>
    /// Cancel all pending work and stop accepting new items.
    /// </summary>
    public void Cancel()
    {
        Volatile.Write(ref _completed, true);
        _channel.Writer.TryComplete();
        _cts.Cancel();
    }

    /// <summary>
    /// Adjust the maximum concurrency at runtime. Safe but intended for rare control-plane changes.
    /// </summary>
    public void SetMaxConcurrency(int newLimit)
    {
        if (!_options.EnableDynamicConcurrency)
            throw new InvalidOperationException("Dynamic concurrency is disabled for this coordinator.");
        _concurrency.UpdateLimit(newLimit);
        Volatile.Write(ref _currentMaxConcurrency, newLimit);
    }

    private async Task ConsumeSourceAsync(IAsyncEnumerable<T> source)
    {
        Exception? sourceException = null;
        try
        {
            await foreach (var item in source.WithCancellation(_cts.Token).ConfigureAwait(false))
            {
                await _channel.Writer.WriteAsync(new WorkItem(item, null), _cts.Token).ConfigureAwait(false);
                Interlocked.Increment(ref _pendingCount);
                Interlocked.Increment(ref _totalEnqueued);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on cancellation
        }
        catch (Exception ex)
        {
            // Capture source enumeration exception to propagate to channel
            sourceException = ex;
        }
        finally
        {
            Volatile.Write(ref _completed, true);
            // Complete channel with exception if source failed, allowing DrainAsync to observe it
            _channel.Writer.TryComplete(sourceException);
        }
    }

    private async Task ProcessAsync()
    {
        try
        {
            await foreach (var work in _channel.Reader.ReadAllAsync(_cts.Token).ConfigureAwait(false))
            {
                // Wait if paused
                _pauseGate.Wait(_cts.Token);

                // Signal-reactive: check if we should skip this item
                if (ShouldCancelDueToSignals())
                {
                    Interlocked.Decrement(ref _pendingCount);
                    Interlocked.Increment(ref _totalFailed); // Count as failed/skipped
                    continue;
                }

                // Signal-reactive: wait if defer signals are present
                await WaitForDeferSignalsAsync(_cts.Token).ConfigureAwait(false);

                await _concurrency.WaitAsync(_cts.Token).ConfigureAwait(false);

                var op = new EphemeralOperation(_options.Signals, _options.OnSignal, _options.OnSignalRetracted, _options.SignalConstraints, work.Id);
                EnqueueOperation(op);
                Interlocked.Decrement(ref _pendingCount);
                Interlocked.Increment(ref _activeTaskCount);

                // Fire-and-forget the execution; we track completion via _activeTaskCount
                _ = ExecuteItemAsync(work.Item, op);
            }

            // Mark channel iteration as complete so task completions can signal drain
            Volatile.Write(ref _channelIterationComplete, true);

            // If all tasks already finished, signal now
            if (Volatile.Read(ref _activeTaskCount) == 0)
            {
                _drainTcs.TrySetResult();
            }

            await _drainTcs.Task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected on cancellation
            _drainTcs.TrySetCanceled();
        }
    }

    private bool ShouldCancelDueToSignals()
    {
        if (_options.CancelOnSignals is not { Count: > 0 })
            return false;

        foreach (var op in _recent)
        {
            if (op._signals is not { Count: > 0 })
                continue;

            foreach (var signal in op._signals)
            {
                if (StringPatternMatcher.MatchesAny(signal, _options.CancelOnSignals))
                    return true;
            }
        }
        return false;
    }

    private async Task WaitForDeferSignalsAsync(CancellationToken ct)
    {
        if (_options.DeferOnSignals is not { Count: > 0 })
            return;

        for (var attempt = 0; attempt < _options.MaxDeferAttempts; attempt++)
        {
            var hasDeferSignal = false;
            foreach (var op in _recent)
            {
                if (op._signals is not { Count: > 0 })
                    continue;

                foreach (var signal in op._signals)
                {
                    if (StringPatternMatcher.MatchesAny(signal, _options.DeferOnSignals))
                    {
                        hasDeferSignal = true;
                        break;
                    }
                }
                if (hasDeferSignal) break;
            }

            if (!hasDeferSignal)
                return; // No defer signals present, proceed

            await Task.Delay(_options.DeferCheckInterval, ct).ConfigureAwait(false);
        }
        // Max attempts reached, proceed anyway
    }

    private async Task ExecuteItemAsync(T item, EphemeralOperation op)
    {
        try
        {
            await _body(item, _cts.Token).ConfigureAwait(false);
            Interlocked.Increment(ref _totalCompleted);
        }
        catch (Exception ex) when (!_cts.Token.IsCancellationRequested)
        {
            op.Error = ex;
            Interlocked.Increment(ref _totalFailed);
        }
        finally
        {
            op.Completed = DateTimeOffset.UtcNow;
            _concurrency.Release();
            CleanupWindow();
            SampleIfRequested();

            // Signal drain completion when last task finishes and channel iteration is done
            // Must be after CleanupWindow/SampleIfRequested so they're complete before DrainAsync returns
            if (Interlocked.Decrement(ref _activeTaskCount) == 0 && Volatile.Read(ref _channelIterationComplete))
            {
                _drainTcs.TrySetResult();
            }
        }
    }

    private void EnqueueOperation(EphemeralOperation op)
    {
        _recent.Enqueue(op);
        CleanupWindow();
    }

    private void CleanupWindow()
    {
        lock (_windowLock)
        {
            TrimWindowSizeLocked();
            TrimWindowAgeLocked();
        }
    }

    private void TrimWindowSizeLocked()
    {
        var max = _options.MaxTrackedOperations;
        if (max <= 0) return;

        // Count pinned operations to avoid infinite cycling
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

                // Stop if we've cycled through all pinned ops
                if (pinnedCount >= _recent.Count)
                    break;
            }
            // Non-pinned ops are dropped (not re-enqueued)
        }
    }

    private void TrimWindowAgeLocked()
    {
        if (_options.MaxOperationLifetime is not { } maxAge)
            return;

        // Throttle age trimming: only run every ~1 second
        var now = Environment.TickCount64;
        var lastTrim = Volatile.Read(ref _lastTrimTicks);
        if (now - lastTrim < 1000)
            return;
        Volatile.Write(ref _lastTrimTicks, now);

        var cutoff = DateTimeOffset.UtcNow - maxAge;
        var scanBudget = _recent.Count;

        while (scanBudget-- > 0 && _recent.TryDequeue(out var op))
        {
            if (op.IsPinned || op.Started >= cutoff)
            {
                _recent.Enqueue(op);
            }
        }
    }

    private void SampleIfRequested()
    {
        var sampler = _options.OnSample;
        if (sampler is null) return;

        var snapshot = _recent.Select(x => x.ToSnapshot()).ToArray();
        if (snapshot.Length > 0)
        {
            sampler(snapshot);
        }
    }

    public async ValueTask DisposeAsync()
    {
        Cancel();
        try
        {
            if (_sourceConsumerTask is not null)
                await _sourceConsumerTask.ConfigureAwait(false);
            await _processingTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected
        }

        await _concurrency.DisposeAsync().ConfigureAwait(false);
        _cts.Dispose();
        _pauseGate.Dispose();
    }

    private static IConcurrencyGate CreateGate(EphemeralOptions options) =>
        options.EnableDynamicConcurrency
            ? new AdjustableConcurrencyGate(options.MaxConcurrency)
            : new FixedConcurrencyGate(options.MaxConcurrency);
}
