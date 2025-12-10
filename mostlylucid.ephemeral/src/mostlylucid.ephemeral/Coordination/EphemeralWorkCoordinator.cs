using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace Mostlylucid.Ephemeral;

/// <summary>
///     A long-lived, observable work coordinator that accepts items continuously.
///     Unlike EphemeralForEachAsync (which processes a collection), this stays alive
///     and lets you enqueue items over time, inspect operations, and gracefully shutdown.
/// </summary>
public sealed class EphemeralWorkCoordinator<T> : IEphemeralCoordinator, IOperationPinning, IOperationFinalization, IOperationEvictor
{
    private readonly Func<T, CancellationToken, Task> _body;

    private readonly Channel<WorkItem> _channel;
    private readonly IConcurrencyGate _concurrency;
    private readonly CancellationTokenSource _cts;
    private readonly TaskCompletionSource _drainTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly OperationEchoStore? _echoStore;
    private readonly EphemeralOptions _options;
    private readonly ManualResetEventSlim _pauseGate = new(true); // true = not paused
    private readonly Task _processingTask;
    private readonly ConcurrentQueue<EphemeralOperation> _recent;
    private readonly Task? _sourceConsumerTask;
    private readonly object _windowLock = new(); // Protects _recent during Evict/Trim operations
    private int _activeTaskCount;
    private bool _channelIterationComplete;
    private bool _completed;
    private int _currentMaxConcurrency;
    private long _lastReadCleanupTicks; // For throttling cleanup on read operations
    private long _lastTrimTicks; // For throttling TrimWindowAge
    private bool _paused;
    private int _pendingCount;
    private int _totalCompleted;
    private int _totalEnqueued;
    private int _totalFailed;

    /// <summary>
    ///     Creates a coordinator that accepts manual enqueues via EnqueueAsync/TryEnqueue.
    /// </summary>
    public EphemeralWorkCoordinator(
        Func<T, CancellationToken, Task> body,
        EphemeralOptions? options = null)
    {
        _body = body ?? throw new ArgumentNullException(nameof(body));
        _options = options ?? new EphemeralOptions();
        _cts = new CancellationTokenSource();
        _recent = new ConcurrentQueue<EphemeralOperation>();
        _echoStore = _options.EnableOperationEcho
            ? new OperationEchoStore(_options.OperationEchoRetention, _options.OperationEchoCapacity)
            : null;
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
    ///     Creates a coordinator that continuously consumes from an IAsyncEnumerable source.
    ///     Runs until the source completes or cancellation is requested.
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
        _echoStore = _options.EnableOperationEcho
            ? new OperationEchoStore(_options.OperationEchoRetention, _options.OperationEchoCapacity)
            : null;
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
    ///     Number of items waiting to be processed.
    /// </summary>
    public int PendingCount => Volatile.Read(ref _pendingCount);

    /// <summary>
    ///     Number of items currently being processed.
    /// </summary>
    public int ActiveCount => Volatile.Read(ref _activeTaskCount);

    /// <summary>
    ///     Total items enqueued since creation.
    /// </summary>
    public int TotalEnqueued => Volatile.Read(ref _totalEnqueued);

    /// <summary>
    ///     Total items completed successfully.
    /// </summary>
    public int TotalCompleted => Volatile.Read(ref _totalCompleted);

    /// <summary>
    ///     Total items that failed with an exception.
    /// </summary>
    public int TotalFailed => Volatile.Read(ref _totalFailed);

    /// <summary>
    ///     Whether Complete() has been called.
    /// </summary>
    public bool IsCompleted => Volatile.Read(ref _completed);

    /// <summary>
    ///     Whether all work is done (completed + drained).
    /// </summary>
    public bool IsDrained => IsCompleted && PendingCount == 0 && ActiveCount == 0;

    /// <summary>
    ///     Whether the coordinator is paused.
    ///     When paused, no new items are pulled from the queue (but running operations continue).
    /// </summary>
    public bool IsPaused => Volatile.Read(ref _paused);

    /// <summary>
    ///     Current max concurrency (tracks dynamic changes when enabled).
    /// </summary>
    public int CurrentMaxConcurrency => Volatile.Read(ref _currentMaxConcurrency);

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

    /// <summary>
    ///     Raised when an operation is removed from the window.
    /// </summary>
    public event Action<EphemeralOperationSnapshot>? OperationFinalized;

    /// <summary>
    ///     Pin an operation by ID so it survives eviction.
    ///     Returns true if found and pinned.
    ///     In single-concurrency pipelines, prefer offloading long-lived/pinned tasks to a sub-coordinator to avoid shrinking
    ///     the active window.
    /// </summary>
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

    /// <summary>
    ///     Unpin an operation by ID, allowing it to be evicted normally.
    ///     Returns true if found and unpinned.
    /// </summary>
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

    /// <summary>
    ///     Creates a coordinator that continuously consumes from an IAsyncEnumerable source.
    ///     Runs until the source completes or cancellation is requested.
    /// </summary>
    public static EphemeralWorkCoordinator<T> FromAsyncEnumerable(
        IAsyncEnumerable<T> source,
        Func<T, CancellationToken, Task> body,
        EphemeralOptions? options = null)
    {
        return new EphemeralWorkCoordinator<T>(source, body, options);
    }

    private void NotifyOperationFinalized(EphemeralOperation op)
    {
        OperationFinalized?.Invoke(op.ToSnapshot());
        RecordEcho(op);
    }

    private void RecordEcho(EphemeralOperation op)
    {
        if (_echoStore is null)
            return;

        var signals = op._signals?.ToArray();
        var echo = new OperationEcho(op.Id, op.Key, signals, DateTimeOffset.UtcNow);
        _echoStore.Add(echo);
    }

    /// <summary>
    ///     Pause processing. Running operations continue, but no new items are started.
    /// </summary>
    public void Pause()
    {
        Volatile.Write(ref _paused, true);
        _pauseGate.Reset();
    }

    /// <summary>
    ///     Resume processing after a pause.
    /// </summary>
    public void Resume()
    {
        Volatile.Write(ref _paused, false);
        _pauseGate.Set();
    }

    /// <summary>
    ///     Gets a snapshot of recent operations (both running and completed).
    ///     Optimized: Manual loop avoids LINQ allocation overhead.
    /// </summary>
    public IReadOnlyCollection<EphemeralOperationSnapshot> GetSnapshot()
    {
        MaybeCleanupForRead();

        // Pre-allocate array with exact capacity
        var count = _recent.Count;
        var result = new EphemeralOperationSnapshot[count];
        var index = 0;

        foreach (var op in _recent)
        {
            if (index < count)
                result[index++] = op.ToSnapshot();
        }

        // Handle race condition where count changed during enumeration
        if (index < count)
            Array.Resize(ref result, index);

        return result;
    }

    /// <summary>
    ///     Gets only the currently running operations.
    ///     Optimized: Manual loop with List pre-sizing avoids LINQ overhead.
    /// </summary>
    public IReadOnlyCollection<EphemeralOperationSnapshot> GetRunning()
    {
        MaybeCleanupForRead();

        // Use List with capacity hint to reduce allocations
        var result = new List<EphemeralOperationSnapshot>(_recent.Count / 2); // Heuristic: ~50% running

        foreach (var op in _recent)
        {
            if (op.Completed is null)
                result.Add(op.ToSnapshot());
        }

        return result;
    }

    /// <summary>
    ///     Gets only the completed operations (success or failure).
    ///     Optimized: Manual loop with List pre-sizing avoids LINQ overhead.
    /// </summary>
    public IReadOnlyCollection<EphemeralOperationSnapshot> GetCompleted()
    {
        MaybeCleanupForRead();

        // Use List with capacity hint to reduce allocations
        var result = new List<EphemeralOperationSnapshot>(_recent.Count / 2); // Heuristic: ~50% completed

        foreach (var op in _recent)
        {
            if (op.Completed is not null)
                result.Add(op.ToSnapshot());
        }

        return result;
    }

    /// <summary>
    ///     Gets only the failed operations.
    ///     Optimized: Manual loop with List pre-sizing avoids LINQ overhead.
    /// </summary>
    public IReadOnlyCollection<EphemeralOperationSnapshot> GetFailed()
    {
        MaybeCleanupForRead();

        // Use List with small capacity hint (failures should be rare)
        var result = new List<EphemeralOperationSnapshot>(_recent.Count / 10); // Heuristic: ~10% failed

        foreach (var op in _recent)
        {
            if (op.Error is not null)
                result.Add(op.ToSnapshot());
        }

        return result;
    }

    /// <summary>
    ///     Throttled cleanup for read operations - only runs every 500ms to reduce lock contention.
    ///     Write operations (enqueue, completion) still trigger immediate cleanup.
    /// </summary>
    private void MaybeCleanupForRead()
    {
        var now = Environment.TickCount64;
        var lastCleanup = Volatile.Read(ref _lastReadCleanupTicks);
        if (now - lastCleanup < 500)
            return; // Skip if we cleaned up recently

        if (Interlocked.CompareExchange(ref _lastReadCleanupTicks, now, lastCleanup) == lastCleanup) CleanupWindow();
    }

    /// <summary>
    ///     Gets all signals from recent operations with their source operation identity.
    /// </summary>
    public IReadOnlyList<SignalEvent> GetSignals()
    {
        return GetSignalsCore(null);
    }

    /// <summary>
    ///     Gets the short-lived echo of signals raised by operations that were just trimmed.
    /// </summary>
    public IReadOnlyList<OperationEcho> GetEchoes()
    {
        return _echoStore?.Snapshot() ?? Array.Empty<OperationEcho>();
    }

    /// <summary>
    ///     Gets signals from operations matching the predicate.
    ///     Use to limit scanning to specific operations (e.g., by key or time range).
    ///     Note: Creates a snapshot for each operation with signals - use specialized overloads for better performance.
    /// </summary>
    public IReadOnlyList<SignalEvent> GetSignals(Func<EphemeralOperationSnapshot, bool>? predicate)
    {
        return GetSignalsCore(predicate);
    }

    /// <summary>
    ///     Gets signals from operations with a specific key.
    ///     Zero-allocation filtering - no snapshot created.
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
            foreach (var signal in op._signals) results.Add(new SignalEvent(signal, op.Id, op.Key, timestamp));
        }

        return results;
    }

    /// <summary>
    ///     Gets signals from operations started within a time range.
    ///     Zero-allocation filtering - no snapshot created.
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
            foreach (var signal in op._signals) results.Add(new SignalEvent(signal, op.Id, op.Key, timestamp));
        }

        return results;
    }

    /// <summary>
    ///     Gets signals from operations started after a specific time.
    ///     Zero-allocation filtering - no snapshot created.
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
            foreach (var signal in op._signals) results.Add(new SignalEvent(signal, op.Id, op.Key, timestamp));
        }

        return results;
    }

    /// <summary>
    ///     Gets signals matching a specific signal name from all operations.
    ///     Zero-allocation filtering - no snapshot created.
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
                if (signal == signalName)
                    results.Add(new SignalEvent(signal, op.Id, op.Key, timestamp));
        }

        return results;
    }

    /// <summary>
    ///     Gets signals matching a pattern (glob-style with * and ?) from all operations.
    ///     Zero-allocation filtering - no snapshot created.
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
    ///     Short-circuits on first match for O(1) best case.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool HasSignal(string signalName)
    {
        foreach (var op in _recent)
        {
            if (op._signals is not { Count: > 0 })
                continue;

            // Manual loop for better performance
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
    ///     Short-circuits on first match for O(1) best case.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool HasSignalMatching(string pattern)
    {
        foreach (var op in _recent)
        {
            if (op._signals is not { Count: > 0 })
                continue;

            // Manual loop for better performance
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
    ///     More efficient than GetSignals().Count as it doesn't allocate SignalEvent structs.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int CountSignals()
    {
        var count = 0;
        foreach (var op in _recent)
            if (op._signals is { Count: > 0 } signals)
                count += signals.Count;
        return count;
    }

    /// <summary>
    ///     Counts signals matching a specific name.
    ///     More efficient than GetSignalsByName().Count.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int CountSignals(string signalName)
    {
        var count = 0;
        foreach (var op in _recent)
        {
            if (op._signals is not { Count: > 0 })
                continue;

            // Manual loop for better performance
            var signals = op._signals;
            var signalCount = signals.Count;
            for (var i = 0; i < signalCount; i++)
                if (signals[i] == signalName)
                    count++;
        }

        return count;
    }

    /// <summary>
    ///     Counts signals matching a pattern.
    ///     More efficient than GetSignalsByPattern().Count.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int CountSignalsMatching(string pattern)
    {
        var count = 0;
        foreach (var op in _recent)
        {
            if (op._signals is not { Count: > 0 })
                continue;

            // Manual loop for better performance
            var signals = op._signals;
            var signalCount = signals.Count;
            for (var i = 0; i < signalCount; i++)
                if (StringPatternMatcher.Matches(signals[i], pattern))
                    count++;
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
            foreach (var signal in op._signals) results.Add(new SignalEvent(signal, op.Id, op.Key, timestamp));
        }

        return results;
    }

    /// <summary>
    ///     Forcibly remove an operation from the window by ID.
    ///     Removes even if pinned. Returns true if found and removed.
    ///     Thread-safe but briefly blocks other window operations.
    /// </summary>
    public bool Evict(long operationId)
    {
        lock (_windowLock)
        {
            // ConcurrentQueue doesn't support removal, so we rebuild without the target
            var toKeep = new List<EphemeralOperation>();
            var found = false;
            while (_recent.TryDequeue(out var op))
                if (op.Id == operationId)
                    found = true;
                // Don't re-add this one
                else
                    toKeep.Add(op);

            foreach (var op in toKeep) _recent.Enqueue(op);
            return found;
        }
    }

    /// <summary>
    ///     Enqueue a new item for processing. Blocks if at capacity.
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
    ///     Enqueue a new item for processing. Blocks if at capacity.
    ///     Returns the operation ID for tracking.
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
    ///     Try to enqueue without blocking. Returns false if at capacity.
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
    ///     Signal that no more items will be added. Processing continues until drained.
    /// </summary>
    public void Complete()
    {
        Volatile.Write(ref _completed, true);
        _channel.Writer.Complete();
    }

    /// <summary>
    ///     Wait for all enqueued work to complete.
    ///     For manual enqueue mode, call Complete() first.
    ///     For IAsyncEnumerable mode, waits for source to complete.
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
    ///     Cancel all pending work and stop accepting new items.
    /// </summary>
    public void Cancel()
    {
        Volatile.Write(ref _completed, true);
        _channel.Writer.TryComplete();
        _cts.Cancel();
    }

    /// <summary>
    ///     Adjust the maximum concurrency at runtime. Safe but intended for rare control-plane changes.
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

                // Signal-reactive: check if we should clear the sink
                CheckClearSignals();

                // Signal-reactive: check if we should begin draining
                CheckDrainSignals();

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

                var op = new EphemeralOperation(_options.Signals, _options.OnSignal, _options.OnSignalRetracted,
                    _options.SignalConstraints, work.Id);
                EnqueueOperation(op);
                Interlocked.Decrement(ref _pendingCount);
                Interlocked.Increment(ref _activeTaskCount);

                // Fire-and-forget the execution; we track completion via _activeTaskCount
                _ = ExecuteItemAsync(work.Item, op);
            }

            // Mark channel iteration as complete so task completions can signal drain
            Volatile.Write(ref _channelIterationComplete, true);

            // If all tasks already finished, signal now
            if (Volatile.Read(ref _activeTaskCount) == 0) _drainTcs.TrySetResult();

            await _drainTcs.Task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected on cancellation
            _drainTcs.TrySetCanceled();
        }
    }

    private void CheckClearSignals()
    {
        if (_options.ClearOnSignals is not { Count: > 0 })
            return;

        if (_options.Signals is null)
            return;

        // Check if any clear signals are present in the window
        foreach (var op in _recent)
        {
            if (op._signals is not { Count: > 0 })
                continue;

            foreach (var signal in op._signals)
            {
                if (StringPatternMatcher.MatchesAny(signal, _options.ClearOnSignals))
                {
                    // Found a clear signal - clear the sink
                    if (_options.ClearOnSignalsUsePattern)
                    {
                        // Extract pattern from signal name (e.g., "clear.errors" → "error.*")
                        // Optimized: Use span-based parsing to avoid String.Split() allocation
                        var firstDot = signal.IndexOf('.');
                        if (firstDot > 0 && signal.AsSpan(0, firstDot).SequenceEqual("clear"))
                        {
                            var pattern = signal.Substring(firstDot + 1) + ".*";
                            _options.Signals.ClearPattern(pattern);
                        }
                        else
                        {
                            // Fallback: clear all
                            _options.Signals.Clear();
                        }
                    }
                    else
                    {
                        // Clear entire sink
                        _options.Signals.Clear();
                    }
                    return;  // Only clear once per check
                }
            }
        }
    }

    private void CheckDrainSignals()
    {
        if (_options.DrainOnSignals is not { Count: > 0 })
            return;

        if (_options.Signals is null)
            return;

        // Already completed? Don't check again
        if (_completed)
            return;

        // Check if any drain signals are present in the global signal window
        var recentSignals = _options.Signals.Sense();

        foreach (var signalEvent in recentSignals)
        {
            if (StringPatternMatcher.MatchesAny(signalEvent.Signal, _options.DrainOnSignals))
            {
                // Check if this drain signal applies to us
                if (signalEvent.Signal == "coordinator.drain.all")
                {
                    // Global drain - applies to everyone
                    Complete();
                    return;
                }
                else if (signalEvent.Signal == "coordinator.drain.id" && _options.CoordinatorId != null)
                {
                    // Targeted drain by ID
                    if (signalEvent.Key == _options.CoordinatorId)
                    {
                        Complete();
                        return;
                    }
                }
                else if (signalEvent.Signal == "coordinator.drain.pattern" && _options.CoordinatorId != null)
                {
                    // Pattern-based drain
                    if (signalEvent.Key != null && StringPatternMatcher.Matches(_options.CoordinatorId, signalEvent.Key))
                    {
                        Complete();
                        return;
                    }
                }
            }
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
                if (StringPatternMatcher.MatchesAny(signal, _options.CancelOnSignals))
                    return true;
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
                    if (StringPatternMatcher.MatchesAny(signal, _options.DeferOnSignals))
                    {
                        hasDeferSignal = true;
                        break;
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
                _drainTcs.TrySetResult();
        }
    }

    private void EnqueueOperation(EphemeralOperation op)
    {
        lock (_windowLock)
        {
            _recent.Enqueue(op);
        }

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
            else
            {
                NotifyOperationFinalized(candidate);
            }
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
            if (op.IsPinned || op.Started >= cutoff)
                _recent.Enqueue(op);
            else
                NotifyOperationFinalized(op);
    }

    private void SampleIfRequested()
    {
        var sampler = _options.OnSample;
        if (sampler is null) return;

        var snapshot = _recent.Select(x => x.ToSnapshot()).ToArray();
        if (snapshot.Length > 0) sampler(snapshot);
    }

    private static IConcurrencyGate CreateGate(EphemeralOptions options)
    {
        return options.EnableDynamicConcurrency
            ? new AdjustableConcurrencyGate(options.MaxConcurrency)
            : new FixedConcurrencyGate(options.MaxConcurrency);
    }

    private readonly record struct WorkItem(T Item, long? Id);

    /// <inheritdoc />
    public bool TryKill(long operationId)
    {
        if (operationId <= 0)
            return false;

        if (!TryRemoveOperation(operationId, out var candidate) || candidate is null)
            return false;

        if (candidate.Completed is null) candidate.Completed = DateTimeOffset.UtcNow;
        candidate.IsPinned = false;
        NotifyOperationFinalized(candidate);
        CleanupWindow();
        return true;
    }

    private bool TryRemoveOperation(long operationId, out EphemeralOperation? removed)
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
}
