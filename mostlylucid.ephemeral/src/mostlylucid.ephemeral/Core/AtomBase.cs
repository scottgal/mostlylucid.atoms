using System.Threading;

namespace Mostlylucid.Ephemeral;

/// <summary>
///     Base class for atoms that wrap ephemeral coordinators.
///     Eliminates boilerplate for common atom patterns (dispose, drain, stats).
///     Atoms own their signals and manage cleanup from the shared SignalSink workspace.
/// </summary>
/// <typeparam name="TCoordinator">The coordinator type being wrapped.</typeparam>
/// <param name="coordinator">The coordinator instance to wrap.</param>
/// <param name="maxSignalCount">Maximum signals this atom can have in the sink (0 = unbounded).</param>
/// <param name="maxSignalAge">Maximum age for this atom's signals (null = unbounded).</param>
public abstract class AtomBase<TCoordinator>(
    TCoordinator coordinator,
    int maxSignalCount = 0,
    TimeSpan? maxSignalAge = null) : IAsyncDisposable
    where TCoordinator : CoordinatorBase
{
    protected readonly TCoordinator Coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));

    private readonly int _maxSignalCount = maxSignalCount;
    private readonly TimeSpan? _maxSignalAge = maxSignalAge;
    private long _lastSignalCleanupTicks;

    /// <summary>
    ///     Disposes the underlying coordinator.
    /// </summary>
    public virtual ValueTask DisposeAsync()
    {
        return Coordinator.DisposeAsync();
    }

    /// <summary>
    ///     Prevents new work and drains outstanding operations.
    /// </summary>
    public virtual async Task DrainAsync(CancellationToken ct = default)
    {
        Complete();
        await DrainInternalAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    ///     Signals completion to stop accepting new items.
    ///     Override if the coordinator needs custom completion logic.
    /// </summary>
    protected virtual void Complete()
    {
        // Most coordinators have Complete() method, but it's not on the base interface
        // Subclasses will override this to call their coordinator's Complete() method
    }

    /// <summary>
    ///     Waits for all operations to complete.
    ///     Override to provide coordinator-specific drain logic.
    /// </summary>
    protected abstract Task DrainInternalAsync(CancellationToken ct);

    /// <summary>
    ///     Returns aggregate statistics (Pending, Active, Completed, Failed).
    /// </summary>
    public virtual (int Pending, int Active, int Completed, int Failed) Stats()
    {
        return (
            Coordinator.PendingCount,
            Coordinator.ActiveCount,
            Coordinator.TotalCompleted,
            Coordinator.TotalFailed
        );
    }

    /// <summary>
    ///     Gets a snapshot of recent operations.
    /// </summary>
    public virtual IReadOnlyCollection<EphemeralOperationSnapshot> Snapshot()
    {
        // Most coordinators have GetSnapshot, but need to call it via reflection or interface
        // For now, return empty - subclasses can override
        return Array.Empty<EphemeralOperationSnapshot>();
    }

    /// <summary>
    ///     Cleans up this atom's signals from the shared sink based on maxSignalCount and maxSignalAge.
    ///     Call this periodically (e.g., after operations complete) to prevent unbounded signal growth.
    ///     For long-lived atoms like scheduled tasks, this prevents signal accumulation.
    /// </summary>
    protected void CleanupAtomSignals()
    {
        // Throttle cleanup to once per second
        var now = Environment.TickCount64;
        var lastCleanup = Volatile.Read(ref _lastSignalCleanupTicks);
        if (now - lastCleanup < 1000)
            return;
        Volatile.Write(ref _lastSignalCleanupTicks, now);

        var sink = Coordinator.Options.Signals;
        if (sink == null)
            return;

        var snapshot = Coordinator.GetSnapshot();
        if (snapshot.Count == 0)
            return;

        var operationIds = snapshot.Select(op => op.Id).ToHashSet();

        // Age-based cleanup: remove signals older than maxSignalAge
        if (_maxSignalAge is { } maxAge)
        {
            var cutoff = DateTimeOffset.UtcNow - maxAge;
            sink.ClearMatching(signal =>
                operationIds.Contains(signal.OperationId) && signal.Timestamp < cutoff);
        }

        // Count-based cleanup: if we have too many signals, remove oldest
        if (_maxSignalCount > 0)
        {
            var atomSignals = sink.Sense(signal => operationIds.Contains(signal.OperationId))
                .OrderBy(s => s.Timestamp)
                .ToList();

            if (atomSignals.Count > _maxSignalCount)
            {
                var toRemove = atomSignals.Take(atomSignals.Count - _maxSignalCount).ToList();
                foreach (var signal in toRemove)
                {
                    // Clear by operation ID to remove all signals from that operation at once
                    sink.ClearOperation(signal.OperationId);
                }
            }
        }
    }
}
