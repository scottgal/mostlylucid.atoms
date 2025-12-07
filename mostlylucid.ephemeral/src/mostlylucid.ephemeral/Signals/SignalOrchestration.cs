using System.Collections.Concurrent;
using System.Linq;
using System.Threading;

namespace Mostlylucid.Ephemeral;

/// <summary>
/// Definition of a stage/wave that triggers when a matching signal is seen.
/// </summary>
public sealed record SignalStage(
    string Name,
    string TriggerPattern,
    Func<CancellationToken, Task> Work,
    IReadOnlyCollection<string>? EmitOnStart = null,
    IReadOnlyCollection<string>? EmitOnComplete = null,
    IReadOnlyCollection<string>? EmitOnFailure = null);

/// <summary>
/// Executes staged work triggered by signals (wave execution). Honors early-exit signals.
/// </summary>
public sealed class SignalWaveExecutor : IAsyncDisposable
{
    private readonly SignalSink _sink;
    private readonly IReadOnlyList<SignalStage> _stages;
    private readonly EphemeralWorkCoordinator<SignalStageInvocation> _coordinator;
    private readonly CancellationTokenSource _cts = new();
    private readonly IReadOnlyCollection<string> _earlyExitPatterns;
    private bool _started;

    public SignalWaveExecutor(
        SignalSink sink,
        IEnumerable<SignalStage> stages,
        IReadOnlyCollection<string>? earlyExitSignals = null,
        int maxConcurrentStages = 4)
    {
        _sink = sink ?? throw new ArgumentNullException(nameof(sink));
        _stages = stages?.ToList() ?? throw new ArgumentNullException(nameof(stages));
        _earlyExitPatterns = earlyExitSignals ?? Array.Empty<string>();
        _coordinator = new EphemeralWorkCoordinator<SignalStageInvocation>(
            async (invocation, ct) => await ExecuteStageAsync(invocation, ct),
            new EphemeralOptions { MaxConcurrency = maxConcurrentStages, Signals = sink });
    }

    /// <summary>
    /// Begin listening for trigger signals.
    /// </summary>
    public void Start()
    {
        if (_started) return;
        _started = true;
        _sink.SignalRaised += OnSignal;
    }

    private void OnSignal(SignalEvent evt)
    {
        if (_cts.IsCancellationRequested)
            return;

        if (_earlyExitPatterns.Any(p => StringPatternMatcher.Matches(evt.Signal, p)))
        {
            _cts.Cancel();
            _sink.Raise($"stage.exit:{evt.Signal}", evt.Key);
            return;
        }

        foreach (var stage in _stages)
        {
            if (!StringPatternMatcher.Matches(evt.Signal, stage.TriggerPattern))
                continue;

            var invocation = new SignalStageInvocation(stage, evt);
            _ = _coordinator.EnqueueAsync(invocation, _cts.Token);
        }
    }

    private async Task ExecuteStageAsync(SignalStageInvocation invocation, CancellationToken ct)
    {
        var stage = invocation.Stage;
        var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token);
        var token = linked.Token;
        var propagation = SignalPropagation.Root(invocation.Trigger.Signal);

        try
        {
            if (stage.EmitOnStart != null)
            {
                foreach (var signal in stage.EmitOnStart)
                    _sink.Raise(new SignalEvent(signal, invocation.Trigger.OperationId, invocation.Trigger.Key, DateTimeOffset.UtcNow, propagation));
            }

            await stage.Work(token).ConfigureAwait(false);

            if (stage.EmitOnComplete != null)
            {
                foreach (var signal in stage.EmitOnComplete)
                    _sink.Raise(new SignalEvent(signal, invocation.Trigger.OperationId, invocation.Trigger.Key, DateTimeOffset.UtcNow, propagation));
            }
        }
        catch (OperationCanceledException)
        {
            _sink.Raise(new SignalEvent($"stage.cancel:{stage.Name}", invocation.Trigger.OperationId, invocation.Trigger.Key, DateTimeOffset.UtcNow, propagation));
        }
        catch (Exception ex)
        {
            if (stage.EmitOnFailure != null)
            {
                foreach (var signal in stage.EmitOnFailure)
                    _sink.Raise(new SignalEvent(signal, invocation.Trigger.OperationId, invocation.Trigger.Key, DateTimeOffset.UtcNow, propagation));
            }

            _sink.Raise(new SignalEvent($"stage.fail:{stage.Name}:{ex.GetType().Name}", invocation.Trigger.OperationId, invocation.Trigger.Key, DateTimeOffset.UtcNow, propagation));
        }
        finally
        {
            linked.Dispose();
        }
    }

    public async ValueTask DisposeAsync()
    {
        _sink.SignalRaised -= OnSignal;
        _cts.Cancel();
        _coordinator.Complete();
        await _coordinator.DrainAsync().ConfigureAwait(false);
        await _coordinator.DisposeAsync().ConfigureAwait(false);
        _cts.Dispose();
    }

    private sealed record SignalStageInvocation(SignalStage Stage, SignalEvent Trigger);
}

/// <summary>
/// Quorum helper: wait for N-of-M signals (pattern-based) with optional cancel/timeout.
/// </summary>
public static class SignalConsensus
{
    public static async Task<QuorumResult> WaitForQuorumAsync(
        SignalSink sink,
        string pattern,
        int required,
        TimeSpan timeout,
        IReadOnlyCollection<string>? cancelOn = null,
        CancellationToken ct = default)
    {
        if (required <= 0) throw new ArgumentOutOfRangeException(nameof(required));

        var cancelPatterns = cancelOn ?? Array.Empty<string>();
        var tcs = new TaskCompletionSource<QuorumResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var seen = new HashSet<long>();

        void Handler(SignalEvent evt)
        {
            if (tcs.Task.IsCompleted)
                return;

            if (cancelPatterns.Any(p => StringPatternMatcher.Matches(evt.Signal, p)))
            {
                tcs.TrySetResult(new QuorumResult(false, seen.Count, CancelSignal: evt.Signal, TimedOut: false));
                return;
            }

            if (!StringPatternMatcher.Matches(evt.Signal, pattern))
                return;

            if (seen.Add(evt.OperationId) && seen.Count >= required)
            {
                tcs.TrySetResult(new QuorumResult(true, seen.Count, null, false));
            }
        }

        sink.SignalRaised += Handler;

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);

        using var registration = timeoutCts.Token.Register(() =>
        {
            tcs.TrySetResult(new QuorumResult(false, seen.Count, null, TimedOut: true));
        });

        try
        {
            return await tcs.Task.ConfigureAwait(false);
        }
        finally
        {
            sink.SignalRaised -= Handler;
        }
    }
}

public readonly record struct QuorumResult(bool Reached, int Count, string? CancelSignal, bool TimedOut);

/// <summary>
/// Emit tiny progress signals (sampled) for UI/monitoring.
/// </summary>
public static class ProgressSignals
{
    private static readonly ConcurrentDictionary<string, int> Counters = new(StringComparer.OrdinalIgnoreCase);

    public static void Emit(SignalSink sink, string key, int current, int total, int sampleRate = 1)
    {
        if (sink is null) throw new ArgumentNullException(nameof(sink));
        sampleRate = Math.Max(1, sampleRate);

        var count = Counters.AddOrUpdate(key, 1, (_, existing) => existing + 1);
        if (current >= total || count % sampleRate == 0)
        {
            sink.Raise(new SignalEvent($"progress:{key}:{current}/{total}", EphemeralIdGenerator.NextId(), key, DateTimeOffset.UtcNow));
        }
    }
}

/// <summary>
/// Time-decaying reputation window (LRU + exponential decay).
/// </summary>
public sealed class DecayingReputationWindow<TKey> where TKey : notnull
{
    private readonly ConcurrentDictionary<TKey, ReputationEntry> _scores = new();
    private readonly SignalSink? _signals;
    private readonly Func<DateTimeOffset> _clock;
    private readonly double _lambda;
    private readonly int _maxSize;

    public DecayingReputationWindow(
        TimeSpan halfLife,
        int maxSize = 1024,
        SignalSink? signals = null,
        Func<DateTimeOffset>? clock = null)
    {
        if (halfLife <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(halfLife));
        _lambda = Math.Log(2) / halfLife.TotalSeconds;
        _maxSize = maxSize;
        _signals = signals;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public double Update(TKey key, double delta)
    {
        var now = _clock();
        var entry = _scores.AddOrUpdate(
            key,
            _ => new ReputationEntry(delta, now),
            (_, existing) => existing.Accumulate(delta, now, _lambda));

        if (_scores.Count > _maxSize)
            TrimOldest();

        _signals?.Raise(new SignalEvent($"reputation.update:{key}:{entry.Score:F2}", EphemeralIdGenerator.NextId(), key?.ToString(), now));
        return entry.Score;
    }

    public double GetScore(TKey key)
    {
        if (!_scores.TryGetValue(key, out var entry))
            return 0;
        var now = _clock();
        return entry.DecayedScore(now, _lambda);
    }

    public void Invalidate(TKey key)
    {
        _scores.TryRemove(key, out _);
        _signals?.Raise(new SignalEvent($"reputation.invalidate:{key}", EphemeralIdGenerator.NextId(), key?.ToString(), _clock()));
    }

    private void TrimOldest()
    {
        // cheap LRU-ish eviction: remove lowest score first
        var victim = _scores.OrderBy(kvp => kvp.Value.Score).FirstOrDefault();
        if (!EqualityComparer<KeyValuePair<TKey, ReputationEntry>>.Default.Equals(victim, default))
            _scores.TryRemove(victim.Key, out _);
    }

    private readonly record struct ReputationEntry(double Score, DateTimeOffset Timestamp)
    {
        public double DecayedScore(DateTimeOffset now, double lambda)
        {
            var dt = (now - Timestamp).TotalSeconds;
            if (dt <= 0) return Score;
            return Score * Math.Exp(-lambda * dt);
        }

        public ReputationEntry Accumulate(double delta, DateTimeOffset now, double lambda)
        {
            var decayed = DecayedScore(now, lambda);
            return new ReputationEntry(decayed + delta, now);
        }
    }
}
