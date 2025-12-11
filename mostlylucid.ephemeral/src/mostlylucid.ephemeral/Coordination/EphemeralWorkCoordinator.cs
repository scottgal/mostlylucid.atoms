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
public sealed class EphemeralWorkCoordinator<T> : CoordinatorBase, IEphemeralCoordinator
{
    private readonly Func<T, CancellationToken, Task> _body;
    private readonly Channel<WorkItem> _channel;
    private readonly IConcurrencyGate _concurrency;
    private readonly Task _processingTask;
    private readonly Task? _sourceConsumerTask;
    private int _currentMaxConcurrency;

    /// <summary>
    ///     Creates a coordinator that accepts manual enqueues via EnqueueAsync/TryEnqueue.
    /// </summary>
    public EphemeralWorkCoordinator(
        Func<T, CancellationToken, Task> body,
        EphemeralOptions? options = null)
        : base(options)
    {
        _body = body ?? throw new ArgumentNullException(nameof(body));
        _concurrency = ConcurrencyHelper.CreateGate(_options);
        _currentMaxConcurrency = _options.MaxConcurrency;

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
        : base(options)
    {
        _body = body ?? throw new ArgumentNullException(nameof(body));
        _concurrency = ConcurrencyHelper.CreateGate(_options);
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
        Dispose(true);
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

    /// <summary>
    ///     Gets a snapshot of recent operations (both running and completed).
    ///     Optimized: Manual loop avoids LINQ allocation overhead.
    /// </summary>
    public override IReadOnlyCollection<EphemeralOperationSnapshot> GetSnapshot()
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
    ///     Gets all signals from recent operations with their source operation identity.
    /// </summary>
    public IReadOnlyList<SignalEvent> GetSignals()
    {
        return GetSignalsCore(null);
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

            foreach (var signalEvt in op._signals) results.Add(signalEvt);
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

            foreach (var signalEvt in op._signals) results.Add(signalEvt);
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

            foreach (var signalEvt in op._signals) results.Add(signalEvt);
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

            foreach (var signalEvt in op._signals)
                if (signalEvt.Signal == signalName)
                    results.Add(signalEvt);
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

            foreach (var signalEvt in op._signals)
                if (StringPatternMatcher.Matches(signalEvt.Signal, pattern))
                    results.Add(signalEvt);
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
                if (signals[i].Signal == signalName)
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
                if (StringPatternMatcher.Matches(signals[i].Signal, pattern))
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
                if (signals[i].Signal == signalName)
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
                if (StringPatternMatcher.Matches(signals[i].Signal, pattern))
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

            foreach (var signalEvt in op._signals) results.Add(signalEvt);
        }

        return results;
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
    ///     Enqueue multiple items for processing in bulk. More efficient than individual enqueues.
    ///     Useful for preloading work with deferred execution (via DeferOnSignals).
    /// </summary>
    public async ValueTask<int> EnqueueManyAsync(IEnumerable<T> items, CancellationToken cancellationToken = default)
    {
        if (_completed)
            throw new InvalidOperationException("Coordinator has been completed; no new items accepted.");

        var count = 0;
        foreach (var item in items)
        {
            await _channel.Writer.WriteAsync(new WorkItem(item, null), cancellationToken).ConfigureAwait(false);
            Interlocked.Increment(ref _pendingCount);
            Interlocked.Increment(ref _totalEnqueued);
            count++;
        }

        return count;
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
    public new void Cancel()
    {
        Volatile.Write(ref _completed, true);
        _channel.Writer.TryComplete();
        base.Cancel();
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

            foreach (var signalEvt in op._signals)
            {
                if (StringPatternMatcher.MatchesAny(signalEvt.Signal, _options.ClearOnSignals))
                {
                    // ClearOnSignals no longer applies - sink doesn't store signals
                    // Operations own their signals; coordinators manage lifetime via eviction
                    // This signal pattern can be used by application logic if needed
                    return;
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

            foreach (var signalEvt in op._signals)
                if (StringPatternMatcher.MatchesAny(signalEvt.Signal, _options.CancelOnSignals))
                    return true;
        }

        return false;
    }

    private async Task WaitForDeferSignalsAsync(CancellationToken ct)
    {
        if (_options.DeferOnSignals is not { Count: > 0 })
            return;

        if (_options.Signals is null)
            return; // No SignalSink, can't defer

        for (var attempt = 0; attempt < _options.MaxDeferAttempts; attempt++)
        {
            var hasDeferSignal = false;
            var hasResumeSignal = false;

            // Check SignalSink for active signals (not operation signals)
            var recentSignals = _options.Signals.Sense();
            foreach (var signalEvent in recentSignals)
            {
                var signal = signalEvent.Signal;

                // Check for resume signals first - they override defer
                if (_options.ResumeOnSignals is { Count: > 0 } &&
                    StringPatternMatcher.MatchesAny(signal, _options.ResumeOnSignals))
                {
                    hasResumeSignal = true;
                    break;
                }

                if (StringPatternMatcher.MatchesAny(signal, _options.DeferOnSignals))
                {
                    hasDeferSignal = true;
                }
            }

            // Resume signal overrides defer
            if (hasResumeSignal)
                return; // Resume signal present, proceed immediately

            if (!hasDeferSignal)
                return; // No defer signals present, proceed

            await Task.Delay(_options.DeferCheckInterval, ct).ConfigureAwait(false);
        }
        // Max attempts reached, proceed anyway
    }

    private async Task ExecuteItemAsync(T item, EphemeralOperation op)
    {
        // Emit automatic lifecycle start signal
        op.Signal("atom.start");

        try
        {
            await _body(item, _cts.Token).ConfigureAwait(false);
            Interlocked.Increment(ref _totalCompleted);
        }
        catch (Exception ex) when (!_cts.Token.IsCancellationRequested)
        {
            op.Error = ex;
            // Store exception in errorstate for easy access
            op.SetState("errorstate", ex);
            Interlocked.Increment(ref _totalFailed);
        }
        finally
        {
            op.Completed = DateTimeOffset.UtcNow;
            // Emit automatic lifecycle stop signal
            op.Signal("atom.stop");

            _concurrency.Release();
            CleanupWindow();
            SampleIfRequested();

            // Signal drain completion when last task finishes and channel iteration is done
            // Must be after CleanupWindow/SampleIfRequested so they're complete before DrainAsync returns
            if (Interlocked.Decrement(ref _activeTaskCount) == 0 && Volatile.Read(ref _channelIterationComplete))
                _drainTcs.TrySetResult();
        }
    }


    private readonly record struct WorkItem(T Item, long? Id);
}
