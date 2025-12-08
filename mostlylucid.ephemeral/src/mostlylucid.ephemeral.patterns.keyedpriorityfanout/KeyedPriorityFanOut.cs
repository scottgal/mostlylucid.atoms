namespace Mostlylucid.Ephemeral.Patterns.KeyedPriorityFanOut;

/// <summary>
///     Pending counts for priority and normal lanes.
/// </summary>
public readonly record struct LaneCounts(int Priority, int Normal);

/// <summary>
///     Keyed fan-out with multiple priority lanes built on PriorityKeyedWorkCoordinator.
///     Priority items drain before normal ones; per-key ordering is preserved inside the coordinator.
/// </summary>
/// <example>
///     <code>
/// var fan = new KeyedPriorityFanOut&lt;UserId, UserCommand&gt;(
///     keySelector: cmd => cmd.UserId,
///     body: HandleCommandAsync,
///     maxConcurrency: 32,
///     perKeyConcurrency: 1);
/// 
/// await fan.EnqueueAsync(cmd);             // normal lane
/// await fan.EnqueuePriorityAsync(cmd);     // jumps the queue for that key
/// 
/// var counts = fan.PendingCounts;          // (Priority: 0, Normal: 5)
/// </code>
/// </example>
public sealed class KeyedPriorityFanOut<TKey, T> : IAsyncDisposable where TKey : notnull
{
    private readonly PriorityKeyedWorkCoordinator<T, TKey> _priorityCoordinator;

    public KeyedPriorityFanOut(
        Func<T, TKey> keySelector,
        Func<T, CancellationToken, Task> body,
        int maxConcurrency,
        int perKeyConcurrency = 1,
        SignalSink? sink = null,
        int? maxPriorityDepth = null,
        IReadOnlySet<string>? cancelPriorityOn = null,
        IReadOnlySet<string>? deferPriorityOn = null)
    {
        var lanes = new[]
        {
            new PriorityLane(
                "priority",
                maxPriorityDepth,
                cancelPriorityOn,
                deferPriorityOn),
            new PriorityLane("normal")
        };

        _priorityCoordinator = new PriorityKeyedWorkCoordinator<T, TKey>(
            new PriorityKeyedWorkCoordinatorOptions<T, TKey>(
                keySelector,
                body,
                lanes,
                new EphemeralOptions
                {
                    MaxConcurrency = maxConcurrency,
                    MaxConcurrencyPerKey = Math.Max(1, perKeyConcurrency),
                    Signals = sink,
                    EnableFairScheduling = false
                }));
    }

    /// <summary>
    ///     Gets pending counts for priority and normal lanes.
    /// </summary>
    public LaneCounts PendingCounts
    {
        get
        {
            var lanes = _priorityCoordinator.PendingCounts;
            var priority = 0;
            var normal = 0;
            foreach (var (lane, count) in lanes)
                if (lane == "priority") priority = count;
                else if (lane == "normal") normal = count;
            return new LaneCounts(priority, normal);
        }
    }

    public ValueTask DisposeAsync()
    {
        return _priorityCoordinator.DisposeAsync();
    }

    /// <summary>
    ///     Enqueue an item into the normal lane.
    /// </summary>
    public ValueTask EnqueueAsync(T item, CancellationToken ct = default)
    {
        return _priorityCoordinator.EnqueueAsync(item, "normal", ct).AsVoid();
    }

    /// <summary>
    ///     Enqueue an item into the priority lane. Returns false if the priority lane is saturated
    ///     (based on MaxDepth, CancelOnSignals, or DeferOnSignals rules).
    /// </summary>
    public ValueTask<bool> EnqueuePriorityAsync(T item, CancellationToken ct = default)
    {
        return _priorityCoordinator.EnqueueAsync(item, "priority", ct);
    }

    public Task DrainAsync(CancellationToken ct = default)
    {
        return _priorityCoordinator.DrainAsync(ct);
    }
}

file static class ValueTaskExtensions
{
    public static async ValueTask AsVoid(this ValueTask<bool> task)
    {
        _ = await task.ConfigureAwait(false);
    }
}