using System.Collections.Concurrent;
using System.Threading.Channels;

namespace Mostlylucid.Ephemeral;

/// <summary>
/// A work coordinator that captures results from each operation.
/// Use this when you need to retrieve outcomes (summaries, fingerprints, IDs) from completed work.
/// </summary>
public sealed class EphemeralResultCoordinator<TInput, TResult> : IAsyncDisposable
{
    private readonly Channel<TInput> _channel;
    private readonly Func<TInput, CancellationToken, Task<TResult>> _body;
    private readonly EphemeralOptions _options;
    private readonly CancellationTokenSource _cts;
    private readonly ConcurrentQueue<EphemeralOperation<TResult>> _recent;
    private readonly IConcurrencyGate _concurrency;
    private readonly ManualResetEventSlim _pauseGate = new(initialState: true);
    private readonly object _windowLock = new();
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
    private long _lastTrimTicks;
    private long _lastReadCleanupTicks;

    public EphemeralResultCoordinator(
        Func<TInput, CancellationToken, Task<TResult>> body,
        EphemeralOptions? options = null)
    {
        _body = body ?? throw new ArgumentNullException(nameof(body));
        _options = options ?? new EphemeralOptions();
        _cts = new CancellationTokenSource();
        _recent = new ConcurrentQueue<EphemeralOperation<TResult>>();
        _concurrency = CreateGate(_options);

        _channel = Channel.CreateBounded<TInput>(new BoundedChannelOptions(_options.MaxTrackedOperations)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = false,
            SingleWriter = false
        });

        _processingTask = ProcessAsync();
    }

    private EphemeralResultCoordinator(
        IAsyncEnumerable<TInput> source,
        Func<TInput, CancellationToken, Task<TResult>> body,
        EphemeralOptions? options)
    {
        _body = body ?? throw new ArgumentNullException(nameof(body));
        _options = options ?? new EphemeralOptions();
        _cts = new CancellationTokenSource();
        _recent = new ConcurrentQueue<EphemeralOperation<TResult>>();
        _concurrency = CreateGate(_options);

        _channel = Channel.CreateBounded<TInput>(new BoundedChannelOptions(_options.MaxTrackedOperations)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = false,
            SingleWriter = false
        });

        _processingTask = ProcessAsync();
        _sourceConsumerTask = ConsumeSourceAsync(source);
    }

    public static EphemeralResultCoordinator<TInput, TResult> FromAsyncEnumerable(
        IAsyncEnumerable<TInput> source,
        Func<TInput, CancellationToken, Task<TResult>> body,
        EphemeralOptions? options = null)
    {
        return new EphemeralResultCoordinator<TInput, TResult>(source, body, options);
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

    public IReadOnlyCollection<EphemeralOperationSnapshot<TResult>> GetSnapshot()
    {
        MaybeCleanupForRead();
        return _recent.Select(x => x.ToSnapshot()).ToArray();
    }

    public IReadOnlyCollection<EphemeralOperationSnapshot> GetBaseSnapshot()
    {
        MaybeCleanupForRead();
        return _recent.Select(x => x.ToBaseSnapshot()).ToArray();
    }

    public IReadOnlyCollection<EphemeralOperationSnapshot<TResult>> GetSuccessful()
    {
        MaybeCleanupForRead();
        return _recent
            .Where(x => x.IsSuccess && x.HasResult)
            .Select(x => x.ToSnapshot())
            .ToArray();
    }

    public IReadOnlyCollection<EphemeralOperationSnapshot<TResult>> GetRunning()
    {
        MaybeCleanupForRead();
        return _recent
            .Where(x => x.Completed is null)
            .Select(x => x.ToSnapshot())
            .ToArray();
    }

    public IReadOnlyCollection<EphemeralOperationSnapshot<TResult>> GetFailed()
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

    public IReadOnlyCollection<TResult> GetResults()
    {
        return _recent
            .Where(x => x.IsSuccess && x.HasResult)
            .Select(x => x.Result!)
            .ToArray();
    }

    public IReadOnlyList<SignalEvent> GetSignals() => GetSignalsCore(null);

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
            if (predicate != null && !predicate(op.ToBaseSnapshot())) continue;
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
            var toKeep = new List<EphemeralOperation<TResult>>();
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

    public async ValueTask EnqueueAsync(TInput item, CancellationToken cancellationToken = default)
    {
        if (_completed)
            throw new InvalidOperationException("Coordinator has been completed; no new items accepted.");
        await _channel.Writer.WriteAsync(item, cancellationToken).ConfigureAwait(false);
        Interlocked.Increment(ref _pendingCount);
        Interlocked.Increment(ref _totalEnqueued);
    }

    public bool TryEnqueue(TInput item)
    {
        if (_completed) return false;
        if (_channel.Writer.TryWrite(item))
        {
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
        _concurrency.UpdateLimit(newLimit);
    }

    private async Task ConsumeSourceAsync(IAsyncEnumerable<TInput> source)
    {
        Exception? sourceException = null;
        try
        {
            await foreach (var item in source.WithCancellation(_cts.Token).ConfigureAwait(false))
            {
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

                if (ShouldCancelDueToSignals())
                {
                    Interlocked.Decrement(ref _pendingCount);
                    Interlocked.Increment(ref _totalFailed);
                    continue;
                }

                await WaitForDeferSignalsAsync(_cts.Token).ConfigureAwait(false);
                await _concurrency.WaitAsync(_cts.Token).ConfigureAwait(false);

                var op = new EphemeralOperation<TResult>(_options.Signals, _options.OnSignal, _options.OnSignalRetracted, _options.SignalConstraints);
                EnqueueOperation(op);
                Interlocked.Decrement(ref _pendingCount);
                Interlocked.Increment(ref _activeTaskCount);

                _ = ExecuteItemAsync(item, op);
            }

            Volatile.Write(ref _channelIterationComplete, true);
            if (Volatile.Read(ref _activeTaskCount) == 0) _drainTcs.TrySetResult();
            await _drainTcs.Task.ConfigureAwait(false);
        }
        catch (OperationCanceledException) { _drainTcs.TrySetCanceled(); }
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

    private async Task ExecuteItemAsync(TInput item, EphemeralOperation<TResult> op)
    {
        try
        {
            var result = await _body(item, _cts.Token).ConfigureAwait(false);
            op.Result = result;
            op.HasResult = true;
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

            if (Interlocked.Decrement(ref _activeTaskCount) == 0 && Volatile.Read(ref _channelIterationComplete))
                _drainTcs.TrySetResult();
        }
    }

    private void EnqueueOperation(EphemeralOperation<TResult> op)
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
        }
    }

    private void SampleIfRequested()
    {
        var sampler = _options.OnSample;
        if (sampler is null) return;
        var snapshot = _recent.Select(x => x.ToBaseSnapshot()).ToArray();
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

        await _concurrency.DisposeAsync().ConfigureAwait(false);
        _cts.Dispose();
        _pauseGate.Dispose();
    }

    private static IConcurrencyGate CreateGate(EphemeralOptions options) =>
        options.EnableDynamicConcurrency
            ? new AdjustableConcurrencyGate(options.MaxConcurrency)
            : new FixedConcurrencyGate(options.MaxConcurrency);
}
