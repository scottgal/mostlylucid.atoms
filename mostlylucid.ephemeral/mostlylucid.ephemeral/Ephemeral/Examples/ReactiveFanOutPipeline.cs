namespace Mostlylucid.Helpers.Ephemeral.Examples;

/// <summary>
/// Reactive fan-out: stage 1 spreads work, but dynamically throttles when stage 2 signals trouble.
/// Stage 2 emits backpressure/failure signals; stage 1 listens and shrinks concurrency until things recover.
/// </summary>
public sealed class ReactiveFanOutPipeline<T> : IAsyncDisposable
{
    private const string BackpressureSignal = "stage2.backpressure";
    private const string FailureSignal = "stage2.failed";
    private const int SampleEvery = 4;

    private readonly EphemeralWorkCoordinator<T> _stage1;
    private readonly EphemeralWorkCoordinator<T> _stage2;
    private readonly Func<T, CancellationToken, Task> _preStageWork;
    private readonly Func<T, CancellationToken, Task> _stage2Work;
    private readonly SignalSink _sink;
    private readonly int _maxStage1Concurrency;
    private readonly int _minStage1Concurrency;
    private readonly int _backpressureThreshold;
    private readonly int _reliefThreshold;
    private readonly int _adjustCooldownMs;
    private int _enqueueCount;
    private long _lastAdjustTicks;
    private long _lastBackpressureRaiseTicks;

    public ReactiveFanOutPipeline(
        Func<T, CancellationToken, Task> stage2Work,
        Func<T, CancellationToken, Task>? preStageWork = null,
        int stage1MaxConcurrency = 8,
        int stage1MinConcurrency = 1,
        int stage2MaxConcurrency = 4,
        int backpressureThreshold = 32,
        int reliefThreshold = 8,
        int adjustCooldownMs = 200,
        SignalSink? sink = null)
    {
        if (stage1MaxConcurrency <= 0) throw new ArgumentOutOfRangeException(nameof(stage1MaxConcurrency));
        if (stage1MinConcurrency <= 0 || stage1MinConcurrency > stage1MaxConcurrency) throw new ArgumentOutOfRangeException(nameof(stage1MinConcurrency));
        if (stage2MaxConcurrency <= 0) throw new ArgumentOutOfRangeException(nameof(stage2MaxConcurrency));
        if (backpressureThreshold <= 0) throw new ArgumentOutOfRangeException(nameof(backpressureThreshold));
        if (reliefThreshold < 0) throw new ArgumentOutOfRangeException(nameof(reliefThreshold));

        _preStageWork = preStageWork ?? ((_, _) => Task.CompletedTask);
        _stage2Work = stage2Work ?? throw new ArgumentNullException(nameof(stage2Work));
        _sink = sink ?? new SignalSink();
        _maxStage1Concurrency = stage1MaxConcurrency;
        _minStage1Concurrency = stage1MinConcurrency;
        _backpressureThreshold = backpressureThreshold;
        _reliefThreshold = reliefThreshold;
        _adjustCooldownMs = adjustCooldownMs;
        _lastAdjustTicks = Environment.TickCount64;
        _lastBackpressureRaiseTicks = Environment.TickCount64;

        _stage2 = new EphemeralWorkCoordinator<T>(
            async (item, ct) =>
            {
                try
                {
                    await _stage2Work(item, ct).ConfigureAwait(false);
                }
                catch
                {
                    _sink.Raise(new SignalEvent(FailureSignal, EphemeralIdGenerator.NextId(), null, DateTimeOffset.UtcNow));
                    throw;
                }
            },
            new EphemeralOptions
            {
                MaxConcurrency = stage2MaxConcurrency,
                Signals = _sink,
                MaxTrackedOperations = backpressureThreshold * 2
            });

        _stage1 = new EphemeralWorkCoordinator<T>(
            async (item, ct) =>
            {
                await _preStageWork(item, ct).ConfigureAwait(false);
                await _stage2.EnqueueAsync(item, ct).ConfigureAwait(false);
                MaybeAdjustConcurrency();
            },
            new EphemeralOptions
            {
                MaxConcurrency = stage1MaxConcurrency,
                EnableDynamicConcurrency = true,
                Signals = _sink,
                MaxTrackedOperations = backpressureThreshold * 2
            });
    }

    public int Stage1CurrentMaxConcurrency => _stage1.CurrentMaxConcurrency;
    public int Stage2Pending => _stage2.PendingCount;

    public ValueTask EnqueueAsync(T item, CancellationToken ct = default)
    {
        var count = Interlocked.Increment(ref _enqueueCount);
        var enqueueTask = _stage1.EnqueueAsync(item, ct);

        // Cheap sampling outside hot path (every few enqueues)
        if ((count & (SampleEvery - 1)) == 0)
            MaybeAdjustConcurrency();

        return enqueueTask;
    }

    private void MaybeAdjustConcurrency()
    {
        var pending = _stage2.PendingCount;
        var nowTicks = Environment.TickCount64;
        var now = DateTimeOffset.UtcNow;
        var recentBackpressure = pending >= _backpressureThreshold;

        if (!recentBackpressure)
        {
            var cutoff = now - TimeSpan.FromMilliseconds(_adjustCooldownMs);
            var signals = _sink.Sense();
            foreach (var s in signals)
            {
                if ((s.Signal == BackpressureSignal || s.Signal == FailureSignal) && s.Timestamp >= cutoff)
                {
                    recentBackpressure = true;
                    break;
                }
            }
        }

        if (recentBackpressure)
        {
            if (_stage1.CurrentMaxConcurrency != _minStage1Concurrency)
                _stage1.SetMaxConcurrency(_minStage1Concurrency);

            if (pending >= _backpressureThreshold && nowTicks - _lastBackpressureRaiseTicks >= 50)
            {
                _sink.Raise(new SignalEvent(BackpressureSignal, EphemeralIdGenerator.NextId(), null, DateTimeOffset.UtcNow));
                _lastBackpressureRaiseTicks = nowTicks;
            }

            _lastAdjustTicks = nowTicks;
            return;
        }

        if (pending <= _reliefThreshold &&
            _stage1.CurrentMaxConcurrency != _maxStage1Concurrency &&
            nowTicks - _lastAdjustTicks >= _adjustCooldownMs)
        {
            _stage1.SetMaxConcurrency(_maxStage1Concurrency);
            _lastAdjustTicks = nowTicks;
        }
    }

    public async Task DrainAsync(CancellationToken ct = default)
    {
        _stage1.Complete();
        _stage2.Complete();
        await _stage1.DrainAsync(ct).ConfigureAwait(false);
        await _stage2.DrainAsync(ct).ConfigureAwait(false);
    }

    public ValueTask DisposeAsync()
    {
        return new ValueTask(Task.WhenAll(_stage1.DisposeAsync().AsTask(), _stage2.DisposeAsync().AsTask()));
    }
}
