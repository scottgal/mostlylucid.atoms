using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading.Channels;

namespace Mostlylucid.Ephemeral;

/// <summary>
/// Keyed version: per-key sequential execution with fair scheduling.
/// </summary>
public sealed class EphemeralKeyedWorkCoordinator<T, TKey> : IAsyncDisposable, IOperationPinning, IOperationFinalization
    where TKey : notnull
{
    /// <summary>
    /// Tracks a per-key semaphore with last usage time for cleanup.
    /// </summary>
    private sealed class KeyLock(SemaphoreSlim gate, int maxCount)
    {
        public SemaphoreSlim Gate { get; } = gate;
        public int MaxCount { get; } = maxCount;
        public long LastUsedTicks = Environment.TickCount64;

        public void Touch() => Volatile.Write(ref LastUsedTicks, Environment.TickCount64);
        public long GetLastUsed() => Volatile.Read(ref LastUsedTicks);
    }

    private readonly Channel<T> _channel;
    private readonly Func<T, TKey> _keySelector;
    private readonly Func<T, CancellationToken, Task> _body;
    private readonly EphemeralOptions _options;
    private readonly CancellationTokenSource _cts;
    private readonly ConcurrentQueue<EphemeralOperation> _recent;
    private readonly IConcurrencyGate _globalConcurrency;
    private readonly ManualResetEventSlim _pauseGate = new(initialState: true);
    private readonly object _windowLock = new();
    private readonly ConcurrentDictionary<TKey, KeyLock> _perKeyLocks;
    private readonly ConcurrentDictionary<TKey, int> _perKeyPendingCount;
    private readonly OperationEchoStore? _echoStore;
    private readonly Task _processingTask;
    private readonly Task? _sourceConsumerTask;
    private readonly TaskCompletionSource _drainTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private const long KeyLockIdleTimeoutMs = 60_000;
    private bool _completed;

    /// <summary>
    /// Raised when an operation is evicted from the window.
    /// </summary>
    public event Action<EphemeralOperationSnapshot>? OperationFinalized;
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
    private bool _channelIterationComplete;
    private bool _paused;
    private int _pendingCount;
    private int _totalEnqueued;
    private int _totalCompleted;
    private int _totalFailed;
    private int _activeTaskCount;
    private long _lastTrimTicks;
    private long _lastReadCleanupTicks;

    public EphemeralKeyedWorkCoordinator(
        Func<T, TKey> keySelector,
        Func<T, CancellationToken, Task> body,
        EphemeralOptions? options = null)
    {
        _keySelector = keySelector ?? throw new ArgumentNullException(nameof(keySelector));
        _body = body ?? throw new ArgumentNullException(nameof(body));
        _options = options ?? new EphemeralOptions();
        _cts = new CancellationTokenSource();
        _recent = new ConcurrentQueue<EphemeralOperation>();
        _globalConcurrency = CreateGate(_options);
        _perKeyLocks = new ConcurrentDictionary<TKey, KeyLock>();
        _perKeyPendingCount = new ConcurrentDictionary<TKey, int>();
        _echoStore = _options.EnableOperationEcho
            ? new OperationEchoStore(_options.OperationEchoRetention, _options.OperationEchoCapacity)
            : null;

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
    {
        _keySelector = keySelector ?? throw new ArgumentNullException(nameof(keySelector));
        _body = body ?? throw new ArgumentNullException(nameof(body));
        _options = options ?? new EphemeralOptions();
        _cts = new CancellationTokenSource();
        _recent = new ConcurrentQueue<EphemeralOperation>();
        _globalConcurrency = CreateGate(_options);
        _perKeyLocks = new ConcurrentDictionary<TKey, KeyLock>();
        _perKeyPendingCount = new ConcurrentDictionary<TKey, int>();
        _echoStore = _options.EnableOperationEcho
            ? new OperationEchoStore(_options.OperationEchoRetention, _options.OperationEchoCapacity)
            : null;

        _channel = Channel.CreateBounded<T>(new BoundedChannelOptions(_options.MaxTrackedOperations)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = false,
            SingleWriter = false
        });

        _processingTask = ProcessAsync();
        _sourceConsumerTask = ConsumeSourceAsync(source);
    }

    public static EphemeralKeyedWorkCoordinator<T, TKey> FromAsyncEnumerable(
        IAsyncEnumerable<T> source,
        Func<T, TKey> keySelector,
        Func<T, CancellationToken, Task> body,
        EphemeralOptions? options = null)
    {
        return new EphemeralKeyedWorkCoordinator<T, TKey>(source, keySelector, body, options);
    }

    public int PendingCount => Volatile.Read(ref _pendingCount);
    public int ActiveCount => Volatile.Read(ref _activeTaskCount);
    public int TotalEnqueued => Volatile.Read(ref _totalEnqueued);
    public int TotalCompleted => Volatile.Read(ref _totalCompleted);
    public int TotalFailed => Volatile.Read(ref _totalFailed);
    public bool IsCompleted => Volatile.Read(ref _completed);
    public bool IsDrained => IsCompleted && PendingCount == 0 && ActiveCount == 0;
    public bool IsPaused => Volatile.Read(ref _paused);

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

    public int GetPendingCountForKey(TKey key) =>
        _perKeyPendingCount.TryGetValue(key, out var count) ? count : 0;

    public IReadOnlyCollection<EphemeralOperationSnapshot> GetSnapshot()
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

    private void MaybeCleanupForRead()
    {
        var now = Environment.TickCount64;
        var lastCleanup = Volatile.Read(ref _lastReadCleanupTicks);
        if (now - lastCleanup < 500) return;
        if (Interlocked.CompareExchange(ref _lastReadCleanupTicks, now, lastCleanup) == lastCleanup)
            CleanupWindow();
    }

    public IReadOnlyList<SignalEvent> GetSignals() => GetSignalsCore(null);

    public IReadOnlyList<SignalEvent> GetSignals(Func<EphemeralOperationSnapshot, bool>? predicate)
        => GetSignalsCore(predicate);

    public IReadOnlyList<SignalEvent> GetSignalsByKey(string key)
    {
        var results = new List<SignalEvent>();
        foreach (var op in _recent)
        {
            if (op._signals is not { Count: > 0 }) continue;
            if (op.Key != key) continue;
            var timestamp = op.Completed ?? op.Started;
            foreach (var signal in op._signals)
                results.Add(new SignalEvent(signal, op.Id, op.Key, timestamp));
        }
        return results;
    }

    /// <summary>
    /// Gets echo copies of signals emitted by operations that were just trimmed.
    /// </summary>
    public IReadOnlyList<OperationEcho> GetEchoes() => _echoStore?.Snapshot() ?? Array.Empty<OperationEcho>();

    public IReadOnlyList<SignalEvent> GetSignalsByKey(TKey key) => GetSignalsByKey(key.ToString()!);

    public IReadOnlyList<SignalEvent> GetSignalsByPattern(string pattern)
    {
        var results = new List<SignalEvent>();
        foreach (var op in _recent)
        {
            if (op._signals is not { Count: > 0 }) continue;
            var timestamp = op.Completed ?? op.Started;
            foreach (var signal in op._signals)
            {
                if (StringPatternMatcher.Matches(signal, pattern))
                    results.Add(new SignalEvent(signal, op.Id, op.Key, timestamp));
            }
        }
        return results;
    }

    public bool HasSignal(string signalName)
    {
        foreach (var op in _recent)
        {
            if (op._signals is not { Count: > 0 }) continue;
            foreach (var signal in op._signals)
                if (signal == signalName) return true;
        }
        return false;
    }

    public bool HasSignalMatching(string pattern)
    {
        foreach (var op in _recent)
        {
            if (op._signals is not { Count: > 0 }) continue;
            foreach (var signal in op._signals)
                if (StringPatternMatcher.Matches(signal, pattern)) return true;
        }
        return false;
    }

    public int CountSignals()
    {
        var count = 0;
        foreach (var op in _recent)
            if (op._signals is { Count: > 0 } signals) count += signals.Count;
        return count;
    }

    private IReadOnlyList<SignalEvent> GetSignalsCore(Func<EphemeralOperationSnapshot, bool>? predicate)
    {
        var results = new List<SignalEvent>();
        foreach (var op in _recent)
        {
            if (op._signals is not { Count: > 0 }) continue;
            if (predicate != null && !predicate(op.ToSnapshot())) continue;
            var timestamp = op.Completed ?? op.Started;
            foreach (var signal in op._signals)
                results.Add(new SignalEvent(signal, op.Id, op.Key, timestamp));
        }
        return results;
    }

    public bool Pin(long operationId)
    {
        foreach (var op in _recent)
            if (op.Id == operationId) { op.IsPinned = true; return true; }
        return false;
    }

    public bool Unpin(long operationId)
    {
        foreach (var op in _recent)
            if (op.Id == operationId) { op.IsPinned = false; return true; }
        return false;
    }

    public bool Evict(long operationId)
    {
        lock (_windowLock)
        {
            var toKeep = new List<EphemeralOperation>();
            var found = false;
            while (_recent.TryDequeue(out var op))
            {
                if (op.Id == operationId) found = true;
                else toKeep.Add(op);
            }
            foreach (var op in toKeep) _recent.Enqueue(op);
            return found;
        }
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

    public async Task DrainAsync(CancellationToken cancellationToken = default)
    {
        if (_sourceConsumerTask is not null)
        {
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _cts.Token);
            await _sourceConsumerTask.WaitAsync(linkedCts.Token).ConfigureAwait(false);
        }
        else if (!_completed)
            throw new InvalidOperationException("Call Complete() before DrainAsync().");

        using var linkedCts2 = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _cts.Token);
        await _processingTask.WaitAsync(linkedCts2.Token).ConfigureAwait(false);
    }

    public void Cancel()
    {
        Volatile.Write(ref _completed, true);
        _channel.Writer.TryComplete();
        _cts.Cancel();
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
        catch (OperationCanceledException) { }
        catch (Exception ex) { sourceException = ex; }
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

                var op = new EphemeralOperation(_options.Signals, _options.OnSignal, _options.OnSignalRetracted, _options.SignalConstraints) { Key = key.ToString() };
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
        catch (OperationCanceledException) { _drainTcs.TrySetCanceled(); }
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
            foreach (var signal in op._signals)
                if (StringPatternMatcher.MatchesAny(signal, _options.CancelOnSignals)) return true;
        }
        return false;
    }

    private async Task WaitForDeferSignalsAsync(CancellationToken ct)
    {
        if (_options.DeferOnSignals is not { Count: > 0 }) return;
        for (var attempt = 0; attempt < _options.MaxDeferAttempts; attempt++)
        {
            var hasDeferSignal = false;
            foreach (var op in _recent)
            {
                if (op._signals is not { Count: > 0 }) continue;
                foreach (var signal in op._signals)
                {
                    if (StringPatternMatcher.MatchesAny(signal, _options.DeferOnSignals))
                    { hasDeferSignal = true; break; }
                }
                if (hasDeferSignal) break;
            }
            if (!hasDeferSignal) return;
            await Task.Delay(_options.DeferCheckInterval, ct).ConfigureAwait(false);
        }
    }

    private async Task ExecuteItemAsync(T item, TKey key, EphemeralOperation op, KeyLock keyLock)
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
        var pinnedCount = 0;
        var scanBudget = _recent.Count;
        while (_recent.Count > max && scanBudget-- > 0)
        {
            if (!_recent.TryDequeue(out var candidate)) break;
            if (candidate.IsPinned)
            {
                pinnedCount++;
                _recent.Enqueue(candidate);
                if (pinnedCount >= _recent.Count) break;
            }
            else
            {
                NotifyOperationFinalized(candidate);
            }
        }
    }

    private void TrimWindowAgeLocked()
    {
        if (_options.MaxOperationLifetime is not { } maxAge) return;
        var now = Environment.TickCount64;
        var lastTrim = Volatile.Read(ref _lastTrimTicks);
        if (now - lastTrim < 1000) return;
        Volatile.Write(ref _lastTrimTicks, now);
        var cutoff = DateTimeOffset.UtcNow - maxAge;
        var scanBudget = _recent.Count;
        while (scanBudget-- > 0 && _recent.TryDequeue(out var op))
        {
            if (op.IsPinned || op.Started >= cutoff) _recent.Enqueue(op);
            else
            {
                NotifyOperationFinalized(op);
            }
        }
    }

    private void SampleIfRequested()
    {
        var sampler = _options.OnSample;
        if (sampler is null) return;
        var snapshot = _recent.Select(x => x.ToSnapshot()).ToArray();
        if (snapshot.Length > 0) sampler(snapshot);
    }

    public async ValueTask DisposeAsync()
    {
        Cancel();
        try
        {
            if (_sourceConsumerTask is not null) await _sourceConsumerTask.ConfigureAwait(false);
            await _processingTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }

        await _globalConcurrency.DisposeAsync().ConfigureAwait(false);
        _cts.Dispose();
        _pauseGate.Dispose();
    }

    private static IConcurrencyGate CreateGate(EphemeralOptions options) =>
        options.EnableDynamicConcurrency
            ? new AdjustableConcurrencyGate(options.MaxConcurrency)
            : new FixedConcurrencyGate(options.MaxConcurrency);
}
