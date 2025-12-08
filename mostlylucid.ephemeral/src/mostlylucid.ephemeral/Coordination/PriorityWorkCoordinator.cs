using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace Mostlylucid.Ephemeral;

/// <summary>
///     Defines a priority lane for a priority-enabled coordinator.
/// </summary>
public sealed record PriorityLane(
    string Name,
    int? MaxDepth = null,
    IReadOnlySet<string>? CancelOnSignals = null,
    IReadOnlySet<string>? DeferOnSignals = null,
    TimeSpan? DeferCheckInterval = null,
    int? MaxDeferAttempts = null)
{
    public string Name { get; init; } = Name ?? throw new ArgumentNullException(nameof(Name));
    public int? MaxDepth { get; init; } = MaxDepth is > 0 ? MaxDepth : null;
    public IReadOnlySet<string>? CancelOnSignals { get; init; } = CancelOnSignals;
    public IReadOnlySet<string>? DeferOnSignals { get; init; } = DeferOnSignals;
    public TimeSpan EffectiveDeferCheckInterval { get; init; } = DeferCheckInterval ?? TimeSpan.FromMilliseconds(100);
    public int EffectiveMaxDeferAttempts { get; init; } = MaxDeferAttempts is > 0 ? MaxDeferAttempts.Value : 50;
}

/// <summary>
///     Options for configuring a priority-enabled unkeyed coordinator.
/// </summary>
public sealed record PriorityWorkCoordinatorOptions<T>(
    Func<T, CancellationToken, Task> Body,
    IReadOnlyCollection<PriorityLane>? Lanes = null,
    EphemeralOptions? EphemeralOptions = null);

/// <summary>
///     Options for configuring a priority-enabled keyed coordinator.
/// </summary>
public sealed record PriorityKeyedWorkCoordinatorOptions<T, TKey>(
    Func<T, TKey> KeySelector,
    Func<T, CancellationToken, Task> Body,
    IReadOnlyCollection<PriorityLane>? Lanes = null,
    EphemeralOptions? EphemeralOptions = null) where TKey : notnull;

/// <summary>
///     Priority wrapper over EphemeralWorkCoordinator. Higher lanes are always drained before lower lanes.
/// </summary>
public sealed class PriorityWorkCoordinator<T> : IAsyncDisposable
{
    private readonly EphemeralWorkCoordinator<T> _coordinator;
    private readonly CancellationTokenSource _cts = new();
    private readonly bool _laneHasSignalRules;
    private readonly Dictionary<string, Lane> _laneLookup;
    private readonly List<Lane> _lanes;
    private readonly TimeSpan _minDeferInterval;
    private readonly Task _pump;
    private readonly SemaphoreSlim _signal = new(0);
    private readonly SignalSink? _signalSource;
    private volatile bool _completed;
    private (string Lane, int Count)[]? _pendingCountsCache;

    public PriorityWorkCoordinator(PriorityWorkCoordinatorOptions<T> options)
    {
        if (options is null) throw new ArgumentNullException(nameof(options));
        _lanes = BuildLanes(options.Lanes);
        _laneLookup = _lanes.ToDictionary(l => l.Definition.Name, StringComparer.Ordinal);
        _signalSource = options.EphemeralOptions?.Signals;
        _laneHasSignalRules = HasAnySignalRules(_lanes);
        if (_laneHasSignalRules && _signalSource is null)
            throw new InvalidOperationException(
                "Lane-level signal gating requires EphemeralOptions.Signals to be set.");
        _minDeferInterval = GetMinDeferInterval(_lanes);

        _coordinator = new EphemeralWorkCoordinator<T>(
            options.Body,
            options.EphemeralOptions ?? new EphemeralOptions());

        _pump = Task.Run(PumpAsync);
    }

    /// <summary>
    ///     Gets pending counts per lane. Reuses a cached array to avoid allocations on repeated calls.
    /// </summary>
    public (string Lane, int Count)[] PendingCounts
    {
        get
        {
            var result = _pendingCountsCache ??= new (string, int)[_lanes.Count];
            for (var i = 0; i < _lanes.Count; i++)
            {
                var lane = _lanes[i];
                result[i] = (lane.Definition.Name, Volatile.Read(ref lane.Count));
            }

            return result;
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _signal.Release();
        try
        {
            await _pump.ConfigureAwait(false);
        }
        catch
        {
            /* ignore */
        }

        _signal.Dispose();
        _cts.Dispose();
        await _coordinator.DisposeAsync();
    }

    /// <summary>
    ///     Enqueue an item into the specified lane (default "normal").
    ///     Returns false if the lane's MaxDepth is exceeded (item rejected).
    /// </summary>
    public ValueTask<bool> EnqueueAsync(T item, string laneName = "normal", CancellationToken ct = default)
    {
        ThrowIfCompleted();
        if (!_laneLookup.TryGetValue(laneName, out var lane))
            throw new ArgumentException($"Lane '{laneName}' is not configured.", nameof(laneName));

        var count = Interlocked.Increment(ref lane.Count);
        if (lane.Definition.MaxDepth is not null && count > lane.Definition.MaxDepth)
        {
            Interlocked.Decrement(ref lane.Count);
            return ValueTask.FromResult(false); // Lane is saturated
        }

        lane.Queue.Enqueue(item);
        _signal.Release();
        return ValueTask.FromResult(true);
    }

    public async Task DrainAsync(CancellationToken ct = default)
    {
        _completed = true;
        _signal.Release();
        await _pump.ConfigureAwait(false);
        _coordinator.Complete();
        await _coordinator.DrainAsync(ct).ConfigureAwait(false);
    }

    private async Task PumpAsync()
    {
        try
        {
            while (true)
            {
                await _signal.WaitAsync(_cts.Token).ConfigureAwait(false);
                var snapshot = _laneHasSignalRules ? _signalSource?.Sense() : null;
                var deferredSeen = false;

                while (true)
                {
                    var dequeued = false;

                    foreach (var lane in _lanes)
                    {
                        if (lane.Definition.CancelOnSignals is { Count: > 0 } &&
                            PriorityLaneHelpers.HasSignal(lane.Definition.CancelOnSignals, snapshot))
                        {
                            while (lane.Queue.TryDequeue(out _)) Interlocked.Decrement(ref lane.Count);
                            lane.DeferAttempts = 0;
                            continue;
                        }

                        if (lane.Definition.DeferOnSignals is { Count: > 0 } &&
                            PriorityLaneHelpers.HasSignal(lane.Definition.DeferOnSignals, snapshot))
                        {
                            lane.DeferAttempts++;
                            if (lane.DeferAttempts < lane.Definition.EffectiveMaxDeferAttempts)
                            {
                                deferredSeen = true;
                                continue;
                            }

                            lane.DeferAttempts = 0; // fall through and run after max attempts
                        }

                        while (lane.Queue.TryDequeue(out var item))
                        {
                            Interlocked.Decrement(ref lane.Count);
                            await _coordinator.EnqueueAsync(item, _cts.Token).ConfigureAwait(false);
                            dequeued = true;
                        }

                        if (!lane.Queue.IsEmpty)
                        {
                            // Lane still has items; check again before touching lower lanes
                            dequeued = true;
                            break;
                        }
                    }

                    if (!dequeued)
                        break;
                }

                if (_completed && AllLanesEmpty())
                    break;

                if (deferredSeen)
                    try
                    {
                        await Task.Delay(_minDeferInterval, _cts.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                    }
            }
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool AllLanesEmpty()
    {
        foreach (var lane in _lanes)
            if (!lane.Queue.IsEmpty)
                return false;
        return true;
    }

    private static List<Lane> BuildLanes(IReadOnlyCollection<PriorityLane>? lanes)
    {
        if (lanes is null || lanes.Count == 0)
            return [new Lane(new PriorityLane("normal"))];

        var result = new List<Lane>(lanes.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var lane in lanes)
        {
            if (string.IsNullOrWhiteSpace(lane.Name))
                throw new ArgumentException("Lane name cannot be empty.");
            if (!seen.Add(lane.Name))
                throw new ArgumentException($"Duplicate lane name: '{lane.Name}'.");
            result.Add(new Lane(lane));
        }

        return result;
    }

    private static bool HasAnySignalRules(List<Lane> lanes)
    {
        foreach (var lane in lanes)
            if (lane.Definition.CancelOnSignals is { Count: > 0 } ||
                lane.Definition.DeferOnSignals is { Count: > 0 })
                return true;
        return false;
    }

    private static TimeSpan GetMinDeferInterval(List<Lane> lanes)
    {
        var min = TimeSpan.FromMilliseconds(100);
        foreach (var lane in lanes)
            if (lane.Definition.EffectiveDeferCheckInterval < min)
                min = lane.Definition.EffectiveDeferCheckInterval;
        return min;
    }

    private void ThrowIfCompleted()
    {
        if (_completed) throw new InvalidOperationException("Coordinator has been completed.");
    }

    private sealed class Lane
    {
        public int Count;
        public int DeferAttempts;

        public Lane(PriorityLane definition)
        {
            Definition = definition;
        }

        public PriorityLane Definition { get; }
        public ConcurrentQueue<T> Queue { get; } = new();
    }
}

/// <summary>
///     Priority wrapper over EphemeralKeyedWorkCoordinator. Higher lanes drain first; per-key ordering still applies
///     inside the coordinator.
/// </summary>
public sealed class PriorityKeyedWorkCoordinator<T, TKey> : IAsyncDisposable where TKey : notnull
{
    private readonly EphemeralKeyedWorkCoordinator<T, TKey> _coordinator;
    private readonly CancellationTokenSource _cts = new();
    private readonly Func<T, TKey> _keySelector;
    private readonly bool _laneHasSignalRules;
    private readonly Dictionary<string, Lane> _laneLookup;
    private readonly List<Lane> _lanes;
    private readonly TimeSpan _minDeferInterval;
    private readonly Task _pump;
    private readonly SemaphoreSlim _signal = new(0);
    private readonly SignalSink? _signalSource;
    private volatile bool _completed;
    private (string Lane, int Count)[]? _pendingCountsCache;

    public PriorityKeyedWorkCoordinator(PriorityKeyedWorkCoordinatorOptions<T, TKey> options)
    {
        if (options is null) throw new ArgumentNullException(nameof(options));
        _keySelector = options.KeySelector ?? throw new ArgumentNullException(nameof(options.KeySelector));
        _lanes = BuildLanes(options.Lanes);
        _laneLookup = _lanes.ToDictionary(l => l.Definition.Name, StringComparer.Ordinal);
        _signalSource = options.EphemeralOptions?.Signals;
        _laneHasSignalRules = HasAnySignalRules(_lanes);
        if (_laneHasSignalRules && _signalSource is null)
            throw new InvalidOperationException(
                "Lane-level signal gating requires EphemeralOptions.Signals to be set.");
        _minDeferInterval = GetMinDeferInterval(_lanes);

        _coordinator = new EphemeralKeyedWorkCoordinator<T, TKey>(
            _keySelector,
            options.Body,
            options.EphemeralOptions ?? new EphemeralOptions());

        _pump = Task.Run(PumpAsync);
    }

    /// <summary>
    ///     Gets pending counts per lane. Reuses a cached array to avoid allocations on repeated calls.
    /// </summary>
    public (string Lane, int Count)[] PendingCounts
    {
        get
        {
            var result = _pendingCountsCache ??= new (string, int)[_lanes.Count];
            for (var i = 0; i < _lanes.Count; i++)
            {
                var lane = _lanes[i];
                result[i] = (lane.Definition.Name, Volatile.Read(ref lane.Count));
            }

            return result;
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _signal.Release();
        try
        {
            await _pump.ConfigureAwait(false);
        }
        catch
        {
            /* ignore */
        }

        _signal.Dispose();
        _cts.Dispose();
        await _coordinator.DisposeAsync();
    }

    /// <summary>
    ///     Enqueue an item into the specified lane (default "normal").
    ///     Returns false if the lane's MaxDepth is exceeded (item rejected).
    /// </summary>
    public ValueTask<bool> EnqueueAsync(T item, string laneName = "normal", CancellationToken ct = default)
    {
        ThrowIfCompleted();
        if (!_laneLookup.TryGetValue(laneName, out var lane))
            throw new ArgumentException($"Lane '{laneName}' is not configured.", nameof(laneName));

        var count = Interlocked.Increment(ref lane.Count);
        if (lane.Definition.MaxDepth is not null && count > lane.Definition.MaxDepth)
        {
            Interlocked.Decrement(ref lane.Count);
            return ValueTask.FromResult(false); // Lane is saturated
        }

        lane.Queue.Enqueue(item);
        _signal.Release();
        return ValueTask.FromResult(true);
    }

    public async Task DrainAsync(CancellationToken ct = default)
    {
        _completed = true;
        _signal.Release();
        await _pump.ConfigureAwait(false);
        _coordinator.Complete();
        await _coordinator.DrainAsync(ct).ConfigureAwait(false);
    }

    private async Task PumpAsync()
    {
        try
        {
            while (true)
            {
                await _signal.WaitAsync(_cts.Token).ConfigureAwait(false);
                var snapshot = _laneHasSignalRules ? _signalSource?.Sense() : null;
                var deferredSeen = false;

                while (true)
                {
                    var dequeued = false;

                    foreach (var lane in _lanes)
                    {
                        if (lane.Definition.CancelOnSignals is { Count: > 0 } &&
                            PriorityLaneHelpers.HasSignal(lane.Definition.CancelOnSignals, snapshot))
                        {
                            while (lane.Queue.TryDequeue(out _)) Interlocked.Decrement(ref lane.Count);
                            lane.DeferAttempts = 0;
                            continue;
                        }

                        if (lane.Definition.DeferOnSignals is { Count: > 0 } &&
                            PriorityLaneHelpers.HasSignal(lane.Definition.DeferOnSignals, snapshot))
                        {
                            lane.DeferAttempts++;
                            if (lane.DeferAttempts < lane.Definition.EffectiveMaxDeferAttempts)
                            {
                                deferredSeen = true;
                                continue;
                            }

                            lane.DeferAttempts = 0;
                        }

                        while (lane.Queue.TryDequeue(out var item))
                        {
                            Interlocked.Decrement(ref lane.Count);
                            await _coordinator.EnqueueAsync(item, _cts.Token).ConfigureAwait(false);
                            dequeued = true;
                        }

                        if (!lane.Queue.IsEmpty)
                        {
                            dequeued = true;
                            break;
                        }
                    }

                    if (!dequeued)
                        break;
                }

                if (_completed && AllLanesEmpty())
                    break;

                if (deferredSeen)
                    try
                    {
                        await Task.Delay(_minDeferInterval, _cts.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                    }
            }
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool AllLanesEmpty()
    {
        foreach (var lane in _lanes)
            if (!lane.Queue.IsEmpty)
                return false;
        return true;
    }

    private static List<Lane> BuildLanes(IReadOnlyCollection<PriorityLane>? lanes)
    {
        if (lanes is null || lanes.Count == 0)
            return [new Lane(new PriorityLane("normal"))];

        var result = new List<Lane>(lanes.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var lane in lanes)
        {
            if (string.IsNullOrWhiteSpace(lane.Name))
                throw new ArgumentException("Lane name cannot be empty.");
            if (!seen.Add(lane.Name))
                throw new ArgumentException($"Duplicate lane name: '{lane.Name}'.");
            result.Add(new Lane(lane));
        }

        return result;
    }

    private static bool HasAnySignalRules(List<Lane> lanes)
    {
        foreach (var lane in lanes)
            if (lane.Definition.CancelOnSignals is { Count: > 0 } ||
                lane.Definition.DeferOnSignals is { Count: > 0 })
                return true;
        return false;
    }

    private static TimeSpan GetMinDeferInterval(List<Lane> lanes)
    {
        var min = TimeSpan.FromMilliseconds(100);
        foreach (var lane in lanes)
            if (lane.Definition.EffectiveDeferCheckInterval < min)
                min = lane.Definition.EffectiveDeferCheckInterval;
        return min;
    }

    private void ThrowIfCompleted()
    {
        if (_completed) throw new InvalidOperationException("Coordinator has been completed.");
    }

    private sealed class Lane
    {
        public int Count;
        public int DeferAttempts;

        public Lane(PriorityLane definition)
        {
            Definition = definition;
        }

        public PriorityLane Definition { get; }
        public ConcurrentQueue<T> Queue { get; } = new();
    }
}

/// <summary>
///     Shared helpers for priority lane signal checking.
/// </summary>
internal static class PriorityLaneHelpers
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool HasSignal(IReadOnlySet<string>? patterns, IReadOnlyList<SignalEvent>? snapshot)
    {
        if (patterns is not { Count: > 0 } || snapshot is null)
            return false;

        foreach (var s in snapshot)
            if (StringPatternMatcher.MatchesAny(s.Signal, patterns))
                return true;
        return false;
    }
}