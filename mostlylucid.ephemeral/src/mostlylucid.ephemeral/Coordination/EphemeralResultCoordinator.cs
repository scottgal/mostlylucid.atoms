using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace Mostlylucid.Ephemeral;

/// <summary>
///     A work coordinator that captures results from each operation.
///     Use this when you need to retrieve outcomes (summaries, fingerprints, IDs) from completed work.
/// </summary>
public sealed class EphemeralResultCoordinator<TInput, TResult> : CoordinatorBase<EphemeralOperation<TResult>>, IEphemeralCoordinator
{
    private readonly Func<TInput, CancellationToken, Task<TResult>> _body;
    private readonly Channel<TInput> _channel;
    private readonly IConcurrencyGate _concurrency;
    private readonly Task _processingTask;
    private readonly Task? _sourceConsumerTask;
    private bool _channelIterationComplete;

    public EphemeralResultCoordinator(
        Func<TInput, CancellationToken, Task<TResult>> body,
        EphemeralOptions? options = null)
        : base(options)
    {
        _body = body ?? throw new ArgumentNullException(nameof(body));
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
        : base(options)
    {
        _body = body ?? throw new ArgumentNullException(nameof(body));
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

    public override async ValueTask DisposeAsync()
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

        await _concurrency.DisposeAsync().ConfigureAwait(false);
        await base.DisposeAsync();
    }

    public static EphemeralResultCoordinator<TInput, TResult> FromAsyncEnumerable(
        IAsyncEnumerable<TInput> source,
        Func<TInput, CancellationToken, Task<TResult>> body,
        EphemeralOptions? options = null)
    {
        return new EphemeralResultCoordinator<TInput, TResult>(source, body, options);
    }

    /// <summary>
    ///     Gets typed snapshot with results.
    /// </summary>
    public IReadOnlyCollection<EphemeralOperationSnapshot<TResult>> GetTypedSnapshot()
    {
        MaybeCleanupForRead();
        return _recent.Select(x => x.ToSnapshot()).ToArray();
    }

    /// <summary>
    ///     Override to provide base snapshot for ICoordinator interface.
    /// </summary>
    public override IReadOnlyCollection<EphemeralOperationSnapshot> GetSnapshot()
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

    public IReadOnlyCollection<TResult> GetResults()
    {
        return _recent
            .Where(x => x.IsSuccess && x.HasResult)
            .Select(x => x.Result!)
            .ToArray();
    }

    public async ValueTask EnqueueAsync(TInput item, CancellationToken cancellationToken = default)
    {
        if (_completed)
            throw new InvalidOperationException("Coordinator has been completed; no new items accepted.");
        await _channel.Writer.WriteAsync(item, cancellationToken).ConfigureAwait(false);
        Interlocked.Increment(ref _pendingCount);
        Interlocked.Increment(ref _totalEnqueued);
    }

    /// <summary>
    ///     Enqueue multiple items for processing in bulk. More efficient than individual enqueues.
    ///     Useful for preloading work with deferred execution (via DeferOnSignals).
    /// </summary>
    public async ValueTask<int> EnqueueManyAsync(IEnumerable<TInput> items, CancellationToken cancellationToken = default)
    {
        if (_completed)
            throw new InvalidOperationException("Coordinator has been completed; no new items accepted.");

        var count = 0;
        foreach (var item in items)
        {
            await _channel.Writer.WriteAsync(item, cancellationToken).ConfigureAwait(false);
            Interlocked.Increment(ref _pendingCount);
            Interlocked.Increment(ref _totalEnqueued);
            count++;
        }

        return count;
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
        {
            throw new InvalidOperationException("Call Complete() before DrainAsync().");
        }

        using var linkedCts2 = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _cts.Token);
        await _processingTask.WaitAsync(linkedCts2.Token).ConfigureAwait(false);
    }

    public void Cancel()
    {
        Volatile.Write(ref _completed, true);
        _channel.Writer.TryComplete();
        _cts.Cancel();
        Signal("coordinator.cancelled");
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

                if (ShouldCancelDueToSignals())
                {
                    Interlocked.Decrement(ref _pendingCount);
                    Interlocked.Increment(ref _totalFailed);
                    continue;
                }

                await WaitForDeferSignalsAsync(_cts.Token).ConfigureAwait(false);
                await _concurrency.WaitAsync(_cts.Token).ConfigureAwait(false);

                var op = new EphemeralOperation<TResult>(_options.Signals, _options.OnSignal,
                    _options.OnSignalRetracted, _options.SignalConstraints);
                EnqueueOperation(op);
                Interlocked.Decrement(ref _pendingCount);
                Interlocked.Increment(ref _activeTaskCount);

                _ = ExecuteItemAsync(item, op);
            }

            Volatile.Write(ref _channelIterationComplete, true);
            if (Volatile.Read(ref _activeTaskCount) == 0) _drainTcs.TrySetResult();
            await _drainTcs.Task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _drainTcs.TrySetCanceled();
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

    private async Task ExecuteItemAsync(TInput item, EphemeralOperation<TResult> op)
    {
        // Emit automatic lifecycle start signal
        op.Signal("atom.start");

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

            if (Interlocked.Decrement(ref _activeTaskCount) == 0 && Volatile.Read(ref _channelIterationComplete))
                _drainTcs.TrySetResult();
        }
    }

    private static IConcurrencyGate CreateGate(EphemeralOptions options)
    {
        return options.EnableDynamicConcurrency
            ? new AdjustableConcurrencyGate(options.MaxConcurrency)
            : new FixedConcurrencyGate(options.MaxConcurrency);
    }
}