using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace Mostlylucid.Ephemeral;

/// <summary>
///     Keyed version: per-key sequential execution with fair scheduling.
/// </summary>
public sealed class EphemeralKeyedWorkCoordinator<T, TKey> : CoordinatorBase
    where TKey : notnull
{
    private const long KeyLockIdleTimeoutMs = 60_000;
    private readonly Func<T, CancellationToken, Task> _body;
    private readonly Channel<T> _channel;
    private readonly IConcurrencyGate _globalConcurrency;
    private readonly Func<T, TKey> _keySelector;
    private readonly ConcurrentDictionary<TKey, KeyLock> _perKeyLocks;
    private readonly ConcurrentDictionary<TKey, int> _perKeyPendingCount;
    private readonly Task _processingTask;
    private readonly Task? _sourceConsumerTask;

    public EphemeralKeyedWorkCoordinator(
        Func<T, TKey> keySelector,
        Func<T, CancellationToken, Task> body,
        EphemeralOptions? options = null)
        : base(options)
    {
        _keySelector = keySelector ?? throw new ArgumentNullException(nameof(keySelector));
        _body = body ?? throw new ArgumentNullException(nameof(body));
        _globalConcurrency = ConcurrencyHelper.CreateGate(_options);
        _perKeyLocks = new ConcurrentDictionary<TKey, KeyLock>();
        _perKeyPendingCount = new ConcurrentDictionary<TKey, int>();

        _channel = Channel.CreateBounded<T>(new BoundedChannelOptions(_options.MaxTrackedOperations)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = false,
            SingleWriter = false
        });

        _processingTask = ProcessAsync();
    }

    private EphemeralKeyedWorkCoordinator(
        IAsyncEnumerable<T> source,
        Func<T, TKey> keySelector,
        Func<T, CancellationToken, Task> body,
        EphemeralOptions? options)
        : base(options)
    {
        _keySelector = keySelector ?? throw new ArgumentNullException(nameof(keySelector));
        _body = body ?? throw new ArgumentNullException(nameof(body));
        _globalConcurrency = ConcurrencyHelper.CreateGate(_options);
        _perKeyLocks = new ConcurrentDictionary<TKey, KeyLock>();
        _perKeyPendingCount = new ConcurrentDictionary<TKey, int>();

        _channel = Channel.CreateBounded<T>(new BoundedChannelOptions(_options.MaxTrackedOperations)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = false,
            SingleWriter = false
        });

        _processingTask = ProcessAsync();
        _sourceConsumerTask = ConsumeSourceAsync(source);
    }

    public async ValueTask DisposeAsync()
    {
        Cancel();
        try
        {
            if (_sourceConsumerTask is not null) await _sourceConsumerTask.ConfigureAwait(false);
            await _processingTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        await _globalConcurrency.DisposeAsync().ConfigureAwait(false);
        Dispose(true);
    }

    public static EphemeralKeyedWorkCoordinator<T, TKey> FromAsyncEnumerable(
        IAsyncEnumerable<T> source,
        Func<T, TKey> keySelector,
        Func<T, CancellationToken, Task> body,
        EphemeralOptions? options = null)
    {
        return new EphemeralKeyedWorkCoordinator<T, TKey>(source, keySelector, body, options);
    }

    public int GetPendingCountForKey(TKey key)
    {
        return _perKeyPendingCount.TryGetValue(key, out var count) ? count : 0;
    }

    public override IReadOnlyCollection<EphemeralOperationSnapshot> GetSnapshot()
    {
        MaybeCleanupForRead();
        return _recent.Select(x => x.ToSnapshot()).ToArray();
    }

    public IReadOnlyCollection<EphemeralOperationSnapshot> GetSnapshotForKey(TKey key)
    {
        MaybeCleanupForRead();
        var keyString = key.ToString();
        return _recent
            .Where(x => x.Key == keyString)
            .Select(x => x.ToSnapshot())
            .ToArray();
    }

    public IReadOnlyCollection<EphemeralOperationSnapshot> GetRunning()
    {
        MaybeCleanupForRead();
        return _recent
            .Where(x => x.Completed is null)
            .Select(x => x.ToSnapshot())
            .ToArray();
    }

    public IReadOnlyCollection<EphemeralOperationSnapshot> GetCompleted()
    {
        MaybeCleanupForRead();
        return _recent
            .Where(x => x.Completed is not null)
            .Select(x => x.ToSnapshot())
            .ToArray();
    }

    public IReadOnlyCollection<EphemeralOperationSnapshot> GetFailed()
    {
        MaybeCleanupForRead();
        return _recent
            .Where(x => x.Error is not null)
            .Select(x => x.ToSnapshot())
            .ToArray();
    }


    public IReadOnlyList<SignalEvent> GetSignalsByKey(TKey key)
    {
        return GetSignalsByKey(key.ToString()!);
    }

    public async ValueTask EnqueueAsync(T item, CancellationToken cancellationToken = default)
    {
        if (_completed)
            throw new InvalidOperationException("Coordinator has been completed; no new items accepted.");
        var key = _keySelector(item);
        _perKeyPendingCount.AddOrUpdate(key, 1, (_, c) => c + 1);
        await _channel.Writer.WriteAsync(item, cancellationToken).ConfigureAwait(false);
        Interlocked.Increment(ref _pendingCount);
        Interlocked.Increment(ref _totalEnqueued);
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
            var key = _keySelector(item);
            _perKeyPendingCount.AddOrUpdate(key, 1, (_, c) => c + 1);
            await _channel.Writer.WriteAsync(item, cancellationToken).ConfigureAwait(false);
            Interlocked.Increment(ref _pendingCount);
            Interlocked.Increment(ref _totalEnqueued);
            count++;
        }

        return count;
    }

    public bool TryEnqueue(T item)
    {
        if (_completed) return false;
        var key = _keySelector(item);
        if (_options.EnableFairScheduling)
        {
            var keyCount = _perKeyPendingCount.GetOrAdd(key, 0);
            if (keyCount >= _options.FairSchedulingThreshold) return false;
        }

        if (_channel.Writer.TryWrite(item))
        {
            _perKeyPendingCount.AddOrUpdate(key, 1, (_, c) => c + 1);
            Interlocked.Increment(ref _pendingCount);
            Interlocked.Increment(ref _totalEnqueued);
            return true;
        }

        return false;
    }

    public void Complete()
    {
        Volatile.Write(ref _completed, true);
        _channel.Writer.Complete();
    }

    public new void Cancel()
    {
        Volatile.Write(ref _completed, true);
        _channel.Writer.TryComplete();
        base.Cancel();
    }

    public async Task DrainAsync(CancellationToken cancellationToken = default)
    {
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

    public void SetMaxConcurrency(int newLimit)
    {
        if (!_options.EnableDynamicConcurrency)
            throw new InvalidOperationException("Dynamic concurrency is disabled for this coordinator.");
        _globalConcurrency.UpdateLimit(newLimit);
    }

    private async Task ConsumeSourceAsync(IAsyncEnumerable<T> source)
    {
        Exception? sourceException = null;
        try
        {
            await foreach (var item in source.WithCancellation(_cts.Token).ConfigureAwait(false))
            {
                var key = _keySelector(item);
                _perKeyPendingCount.AddOrUpdate(key, 1, (_, c) => c + 1);
                await _channel.Writer.WriteAsync(item, _cts.Token).ConfigureAwait(false);
                Interlocked.Increment(ref _pendingCount);
                Interlocked.Increment(ref _totalEnqueued);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            sourceException = ex;
        }
        finally
        {
            Volatile.Write(ref _completed, true);
            _channel.Writer.TryComplete(sourceException);
        }
    }

    private async Task ProcessAsync()
    {
        try
        {
            await foreach (var item in _channel.Reader.ReadAllAsync(_cts.Token).ConfigureAwait(false))
            {
                _pauseGate.Wait(_cts.Token);
                var key = _keySelector(item);

                if (ShouldCancelDueToSignals())
                {
                    Interlocked.Decrement(ref _pendingCount);
                    _perKeyPendingCount.AddOrUpdate(key, 0, (_, c) => Math.Max(0, c - 1));
                    Interlocked.Increment(ref _totalFailed);
                    continue;
                }

                await WaitForDeferSignalsAsync(_cts.Token).ConfigureAwait(false);

                var keyLock = _perKeyLocks.GetOrAdd(key, _ =>
                    new KeyLock(new SemaphoreSlim(_options.MaxConcurrencyPerKey), _options.MaxConcurrencyPerKey));

                await _globalConcurrency.WaitAsync(_cts.Token).ConfigureAwait(false);
                await keyLock.Gate.WaitAsync(_cts.Token).ConfigureAwait(false);
                keyLock.Touch();

                var op = new EphemeralOperation(_options.Signals, _options.OnSignal, _options.OnSignalRetracted,
                    _options.SignalConstraints) { Key = key.ToString() };
                EnqueueOperation(op);
                Interlocked.Decrement(ref _pendingCount);
                _perKeyPendingCount.AddOrUpdate(key, 0, (_, c) => Math.Max(0, c - 1));
                Interlocked.Increment(ref _activeTaskCount);

                _ = ExecuteItemAsync(item, key, op, keyLock);
            }

            Volatile.Write(ref _channelIterationComplete, true);
            if (Volatile.Read(ref _activeTaskCount) == 0) _drainTcs.TrySetResult();
            await _drainTcs.Task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _drainTcs.TrySetCanceled();
        }
        finally
        {
            foreach (var keyLock in _perKeyLocks.Values) keyLock.Gate.Dispose();
        }
    }

    private bool ShouldCancelDueToSignals()
    {
        if (_options.CancelOnSignals is not { Count: > 0 }) return false;
        foreach (var op in _recent)
        {
            if (op._signals is not { Count: > 0 }) continue;
            foreach (var signalEvt in op._signals)
                if (StringPatternMatcher.MatchesAny(signalEvt.Signal, _options.CancelOnSignals))
                    return true;
        }

        return false;
    }

    private async Task WaitForDeferSignalsAsync(CancellationToken ct)
    {
        if (_options.DeferOnSignals is not { Count: > 0 }) return;
        if (_options.Signals is null) return;

        for (var attempt = 0; attempt < _options.MaxDeferAttempts; attempt++)
        {
            var hasDeferSignal = false;
            var hasResumeSignal = false;

            var recentSignals = _options.Signals.Sense();
            foreach (var signalEvent in recentSignals)
            {
                var signal = signalEvent.Signal;
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

            if (hasResumeSignal) return;
            if (!hasDeferSignal) return;
            await Task.Delay(_options.DeferCheckInterval, ct).ConfigureAwait(false);
        }
    }

    private async Task ExecuteItemAsync(T item, TKey key, EphemeralOperation op, KeyLock keyLock)
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

            keyLock.Gate.Release();
            keyLock.Touch();
            _globalConcurrency.Release();
            CleanupWindow();
            SampleIfRequested();
            CleanupIdleKeyLocks(key, keyLock);

            if (Interlocked.Decrement(ref _activeTaskCount) == 0 && Volatile.Read(ref _channelIterationComplete))
                _drainTcs.TrySetResult();
        }
    }

    private void CleanupIdleKeyLocks(TKey currentKey, KeyLock currentKeyLock)
    {
        var now = Environment.TickCount64;
        if ((now & 0x3FF) != 0) return;

        foreach (var kvp in _perKeyLocks)
        {
            var key = kvp.Key;
            var keyLock = kvp.Value;
            if (EqualityComparer<TKey>.Default.Equals(key, currentKey)) continue;
            var idleTime = now - keyLock.GetLastUsed();
            if (idleTime < KeyLockIdleTimeoutMs) continue;
            if (keyLock.Gate.CurrentCount != keyLock.MaxCount) continue;
            if (_perKeyLocks.TryRemove(kvp))
            {
                _perKeyPendingCount.TryRemove(key, out _);
                keyLock.Gate.Dispose();
            }
        }
    }


    /// <summary>
    ///     Tracks a per-key semaphore with last usage time for cleanup.
    /// </summary>
    private sealed class KeyLock(SemaphoreSlim gate, int maxCount)
    {
        public long LastUsedTicks = Environment.TickCount64;
        public SemaphoreSlim Gate { get; } = gate;
        public int MaxCount { get; } = maxCount;

        public void Touch()
        {
            Volatile.Write(ref LastUsedTicks, Environment.TickCount64);
        }

        public long GetLastUsed()
        {
            return Volatile.Read(ref LastUsedTicks);
        }
    }
}
